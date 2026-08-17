package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"go/ast"
	"go/format"
	"go/parser"
	"go/token"
	"path"
	"sort"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf8"
)

type moduleInfo struct {
	Path string
	Root string
}

type parsedGoFile struct {
	file        snapshotFile
	path        string
	module      moduleInfo
	importPath  string
	packageName string
	tree        *ast.File
	imports     map[string]string
	fileUnit    string
	packageUnit string
}

type symbolTable struct {
	functions map[string]string // import path/name -> member identity
	types     map[string]string // import path/name -> type identity
	methods   map[string]string // import path/receiver/name -> member identity
}

func indexGo(snapshot repositorySnapshot) adapterIndex {
	result, err := indexGoScoped(snapshot, "")
	if err != nil {
		return adapterIndex{Warnings: []string{err.Error()}}
	}
	return result
}

func indexGoScoped(snapshot repositorySnapshot, configured string) (adapterIndex, error) {
	scoped, err := selectScope(snapshot, configured)
	if err != nil {
		return adapterIndex{}, err
	}
	files := append([]snapshotFile(nil), scoped.Files...)
	for i := range files {
		files[i].Path = normalizePath(files[i].Path)
	}
	modules := discoverModules(files)
	units := make([]sourceUnit, 0)
	edges := make([]impactEdge, 0)
	warnings := make([]string, 0)
	projectRoots := map[string]string{}
	for _, file := range files {
		base := path.Base(file.Path)
		if base == "go.mod" || base == "go.work" || base == "go.sum" || base == "go.work.sum" {
			id := "golang:project:" + file.Path
			units = append(units, sourceUnit{id, "project", file.Path, contentHash(file), contentHash(file)})
			if base == "go.mod" {
				projectRoots[path.Dir(file.Path)] = id
			}
		}
	}
	parsed := make([]parsedGoFile, 0)
	for _, file := range files {
		if !strings.HasSuffix(file.Path, ".go") {
			continue
		}
		fileID := "golang:file:" + file.Path
		units = append(units, sourceUnit{fileID, "file", file.Path, contentHash(file), contentHash(file)})
		if isGeneratedGo(file.Content) {
			warnings = append(warnings, "Generated file was preserved as a file unit: "+file.Path)
		}
		if hasBuildTag(file.Content) {
			warnings = append(warnings, "Build-tagged file may be configuration-dependent: "+file.Path)
		}
		module := moduleFor(file.Path, modules)
		fset := token.NewFileSet()
		tree, err := parser.ParseFile(fset, file.Path, file.Content, parser.ParseComments)
		if err != nil {
			warnings = append(warnings, "Could not parse "+file.Path+": "+err.Error())
			continue
		}
		pkg := packageImportPath(file.Path, module)
		pf := parsedGoFile{file: file, path: file.Path, module: module, importPath: pkg, packageName: tree.Name.Name, tree: tree,
			imports: importsFor(tree), fileUnit: fileID, packageUnit: "golang:package:" + pkg}
		parsed = append(parsed, pf)
	}

	packages := map[string]bool{}
	for _, pf := range parsed {
		packages[pf.importPath] = true
	}
	for pkg := range packages {
		packageFilePath := packagePath(pkg)
		for _, pf := range parsed {
			if pf.importPath == pkg {
				packageFilePath = path.Dir(pf.path)
				break
			}
		}
		units = append(units, sourceUnit{"golang:package:" + pkg, "namespace", packageFilePath, pkg, pkg})
		if project, ok := projectForPackage(pkg, modules, projectRoots); ok {
			edges = append(edges, impactEdge{"golang:package:" + pkg, project, "containment"})
		}
	}

	table := symbolTable{functions: map[string]string{}, types: map[string]string{}, methods: map[string]string{}}
	// First pass establishes all declarations, allowing forward and cross-file resolution.
	for _, pf := range parsed {
		collectSymbols(pf, &units, &edges, &table)
	}
	for _, pf := range parsed {
		edges = append(edges, impactEdge{pf.fileUnit, pf.packageUnit, "containment"})
		for alias, imported := range pf.imports {
			_ = alias
			if imported != pf.importPath {
				edges = append(edges, impactEdge{pf.packageUnit, "golang:package:" + imported, "staticDependency"})
			}
		}
		indexCalls(pf, table, &edges, &warnings)
		collectTests(pf, &units, &edges)
	}

	units = uniqueUnits(units)
	edges = uniqueEdges(edges)
	sortIndex(&units, &edges, &warnings)
	return adapterIndex{Units: units, Edges: edges, Tests: collectTestDescriptors(units, edges, parsed), Warnings: sortStringsUnique(warnings)}, nil
}

func selectScope(snapshot repositorySnapshot, configured string) (repositorySnapshot, error) {
	files := append([]snapshotFile(nil), snapshot.Files...)
	for i := range files {
		files[i].Path = normalizePath(files[i].Path)
	}
	configured = normalizePath(configured)
	workspaces := []string{}
	for _, file := range files {
		if path.Base(file.Path) == "go.work" {
			workspaces = append(workspaces, file.Path)
		}
	}
	if configured == "" && len(workspaces) == 1 {
		configured = workspaces[0]
	}
	if configured == "" {
		snapshot.Files = files
		return snapshot, nil
	}
	selected := false
	for _, file := range files {
		if file.Path == configured {
			selected = true
			break
		}
	}
	if !selected {
		return repositorySnapshot{}, fmt.Errorf("configured Go scope %q is not present in the snapshot", configured)
	}
	switch path.Base(configured) {
	case "go.mod":
		return scopedModuleSnapshot(snapshot, files, configured), nil
	case "go.work":
		return scopedWorkspaceSnapshot(snapshot, files, configured)
	default:
		return repositorySnapshot{}, fmt.Errorf("configured Go scope %q must be a go.mod or go.work", configured)
	}
}

func scopedModuleSnapshot(snapshot repositorySnapshot, files []snapshotFile, manifest string) repositorySnapshot {
	root := path.Dir(manifest)
	nested := []string{}
	for _, file := range files {
		if path.Base(file.Path) == "go.mod" && file.Path != manifest && pathWithin(file.Path, root) {
			nested = append(nested, path.Dir(file.Path))
		}
	}
	selected := make([]snapshotFile, 0)
	for _, file := range files {
		if !pathWithin(file.Path, root) || path.Base(file.Path) == "go.work" || path.Base(file.Path) == "go.work.sum" {
			continue
		}
		excluded := false
		for _, nestedRoot := range nested {
			if pathWithin(file.Path, nestedRoot) {
				excluded = true
				break
			}
		}
		if !excluded {
			selected = append(selected, file)
		}
	}
	snapshot.Files = selected
	return snapshot
}

func scopedWorkspaceSnapshot(snapshot repositorySnapshot, files []snapshotFile, workspace string) (repositorySnapshot, error) {
	workspaceRoot := path.Dir(workspace)
	roots := workspaceRootsForFiles(files, workspace)
	if len(roots) == 0 {
		return repositorySnapshot{}, fmt.Errorf("configured Go workspace %q has no usable modules", workspace)
	}
	listed := map[string]bool{}
	for _, root := range roots {
		if root == ".." || strings.HasPrefix(root, "../") || strings.HasPrefix(root, "/") {
			return repositorySnapshot{}, fmt.Errorf("go.work use path %q escapes the repository", root)
		}
		manifest := "go.mod"
		if root != "" {
			manifest = root + "/go.mod"
		}
		foundManifest := false
		for _, file := range files {
			if file.Path == manifest {
				foundManifest = true
				break
			}
		}
		if !foundManifest {
			return repositorySnapshot{}, fmt.Errorf("go.work use path %q has no matching go.mod", root)
		}
		listed[root] = true
	}
	nestedModules := []string{}
	for _, file := range files {
		if path.Base(file.Path) == "go.mod" {
			moduleRoot := path.Dir(file.Path)
			if moduleRoot == "." {
				moduleRoot = ""
			}
			nestedModules = append(nestedModules, moduleRoot)
		}
	}
	selected := make([]snapshotFile, 0)
	for _, file := range files {
		if file.Path == workspace || (path.Dir(file.Path) == workspaceRoot && path.Base(file.Path) == "go.work.sum") {
			selected = append(selected, file)
			continue
		}
		for _, root := range roots {
			if pathWithin(file.Path, root) {
				excluded := false
				for _, moduleRoot := range nestedModules {
					if moduleRoot != root && !listed[moduleRoot] && pathWithin(moduleRoot, root) && pathWithin(file.Path, moduleRoot) {
						excluded = true
						break
					}
				}
				if excluded {
					continue
				}
				selected = append(selected, file)
				break
			}
		}
	}
	snapshot.Files = selected
	return snapshot, nil
}

func pathWithin(filePath, root string) bool {
	filePath, root = normalizePath(filePath), normalizePath(root)
	if root == "." || root == "" {
		return true
	}
	return filePath == root || strings.HasPrefix(filePath, root+"/")
}

func workspaceRootsForFiles(files []snapshotFile, requested string) []string {
	for _, file := range files {
		if file.Path != requested {
			continue
		}
		base := path.Dir(requested)
		roots := []string{}
		inBlock := false
		for _, raw := range strings.Split(string(file.Content), "\n") {
			line := strings.TrimSpace(strings.SplitN(raw, "//", 2)[0])
			if line == "use (" {
				inBlock = true
				continue
			}
			if inBlock && line == ")" {
				inBlock = false
				continue
			}
			value := ""
			if inBlock {
				fields := strings.Fields(line)
				if len(fields) != 0 {
					value = fields[0]
				}
			} else if strings.HasPrefix(line, "use ") {
				value = strings.TrimSpace(strings.TrimPrefix(line, "use "))
			}
			if value == "" {
				continue
			}
			root := normalizePath(path.Join(base, strings.Trim(value, "\"")))
			if root == "." {
				root = ""
			}
			roots = append(roots, root)
		}
		return sortStringsUnique(roots)
	}
	return nil
}

func discoverModules(files []snapshotFile) []moduleInfo {
	modules := make([]moduleInfo, 0)
	for _, file := range files {
		if path.Base(file.Path) != "go.mod" {
			continue
		}
		modulePath := ""
		for _, line := range strings.Split(string(file.Content), "\n") {
			fields := strings.Fields(strings.TrimSpace(strings.SplitN(line, "//", 2)[0]))
			if len(fields) >= 2 && fields[0] == "module" {
				modulePath = fields[1]
				break
			}
		}
		if modulePath == "" {
			continue
		}
		modules = append(modules, moduleInfo{modulePath, path.Dir(file.Path)})
	}
	// Longest root wins for nested/multi-module repositories.
	for i := range modules {
		for j := i + 1; j < len(modules); j++ {
			if len(modules[j].Root) > len(modules[i].Root) {
				modules[i], modules[j] = modules[j], modules[i]
			}
		}
	}
	return modules
}

func moduleFor(filePath string, modules []moduleInfo) moduleInfo {
	for _, module := range modules {
		if module.Root == "." || filePath == module.Root || strings.HasPrefix(filePath, module.Root+"/") {
			return module
		}
	}
	return moduleInfo{}
}

func packageImportPath(filePath string, module moduleInfo) string {
	dir := path.Dir(filePath)
	if module.Path == "" {
		return strings.TrimPrefix(dir, "./")
	}
	rel := strings.TrimPrefix(dir, module.Root)
	rel = strings.TrimPrefix(rel, "/")
	if rel == "" || rel == "." {
		return module.Path
	}
	return strings.TrimSuffix(module.Path, "/") + "/" + rel
}

func packagePath(pkg string) string {
	if i := strings.LastIndex(pkg, "/"); i >= 0 {
		return pkg[:i+1]
	}
	return "."
}

func projectForPackage(pkg string, modules []moduleInfo, projects map[string]string) (string, bool) {
	for _, module := range modules {
		if pkg == module.Path || strings.HasPrefix(pkg, module.Path+"/") {
			if id, ok := projects[module.Root]; ok {
				return id, true
			}
		}
	}
	return "", false
}

func importsFor(file *ast.File) map[string]string {
	imports := map[string]string{}
	for _, spec := range file.Imports {
		name := ""
		if spec.Name != nil {
			name = spec.Name.Name
		}
		value, err := strconv.Unquote(spec.Path.Value)
		if err != nil {
			continue
		}
		if name == "" {
			name = path.Base(value)
		}
		if name != "_" && name != "." {
			imports[name] = value
		}
	}
	return imports
}

func collectSymbols(pf parsedGoFile, units *[]sourceUnit, edges *[]impactEdge, table *symbolTable) {
	for _, decl := range pf.tree.Decls {
		switch d := decl.(type) {
		case *ast.GenDecl:
			if d.Tok.String() != "type" {
				continue
			}
			for _, spec := range d.Specs {
				ts, ok := spec.(*ast.TypeSpec)
				if !ok {
					continue
				}
				typeID := "golang:type:" + pf.importPath + "/" + ts.Name.Name
				sig := nodeSignature(ts)
				*units = append(*units, sourceUnit{typeID, "type", pf.path, sig, sig})
				*edges = append(*edges, impactEdge{typeID, pf.packageUnit, "containment"})
				table.types[pf.importPath+"/"+ts.Name.Name] = typeID
				if iface, ok := ts.Type.(*ast.InterfaceType); ok {
					for _, field := range iface.Methods.List {
						if len(field.Names) == 0 {
							continue
						}
						if fn, ok := field.Type.(*ast.FuncType); ok {
							methodID := "golang:member:" + pf.importPath + "/" + ts.Name.Name + "/" + field.Names[0].Name + "(" + funcParams(fn) + ")"
							methodSig := nodeSignature(field)
							*units = append(*units, sourceUnit{methodID, "member", pf.path, methodSig, methodSig})
							*edges = append(*edges, impactEdge{methodID, typeID, "containment"})
							table.methods[pf.importPath+"/"+ts.Name.Name+"/"+field.Names[0].Name] = methodID
						}
					}
				}
			}
		case *ast.FuncDecl:
			if d.Recv == nil {
				id := funcIdentity(pf, d)
				sig := nodeSignature(d)
				*units = append(*units, sourceUnit{id, "member", pf.path, sig, sig})
				*edges = append(*edges, impactEdge{id, pf.packageUnit, "containment"})
				table.functions[pf.importPath+"/"+d.Name.Name] = id
			} else {
				receiver := receiverName(d.Recv.List[0].Type)
				id := funcIdentity(pf, d)
				sig := nodeSignature(d)
				*units = append(*units, sourceUnit{id, "member", pf.path, sig, sig})
				typeID := "golang:type:" + pf.importPath + "/" + receiver
				*edges = append(*edges, impactEdge{id, typeID, "containment"})
				table.methods[pf.importPath+"/"+receiver+"/"+d.Name.Name] = id
			}
		}
	}
}

func collectTests(pf parsedGoFile, units *[]sourceUnit, edges *[]impactEdge) {
	if !strings.HasSuffix(pf.path, "_test.go") {
		return
	}
	for _, decl := range pf.tree.Decls {
		fn, ok := decl.(*ast.FuncDecl)
		if !ok || fn.Recv != nil || !isGoTestFunc(fn, pf.imports) {
			continue
		}
		memberID := memberForFunc(pf, fn)
		testID := "golang:" + pf.importPath + ":" + fn.Name.Name
		*edges = append(*edges, impactEdge{memberID, testID, "staticDependency"})
		// Keep an explicit test identity in the graph without duplicating it as a source unit.
	}
}

func isGoTestName(name string) bool {
	for _, prefix := range []string{"Test", "Benchmark", "Fuzz", "Example"} {
		if name == prefix {
			return true
		}
		if strings.HasPrefix(name, prefix) && len(name) > len(prefix) {
			next, _ := utf8.DecodeRuneInString(name[len(prefix):])
			return !unicode.IsLower(next)
		}
	}
	return false
}

func isGoTestFunc(fn *ast.FuncDecl, imports map[string]string) bool {
	if !isGoTestName(fn.Name.Name) {
		return false
	}
	if fn.Type.Results != nil && len(fn.Type.Results.List) != 0 {
		return false
	}
	if strings.HasPrefix(fn.Name.Name, "Example") {
		return fn.Type.Params == nil || len(fn.Type.Params.List) == 0
	}
	if fn.Type.Params == nil || len(fn.Type.Params.List) != 1 {
		return false
	}
	field := fn.Type.Params.List[0]
	if len(field.Names) > 1 {
		return false
	}
	ptr, ok := field.Type.(*ast.StarExpr)
	if !ok {
		return false
	}
	selector, ok := ptr.X.(*ast.SelectorExpr)
	if !ok {
		return false
	}
	packageName, ok := selector.X.(*ast.Ident)
	if !ok || imports[packageName.Name] != "testing" {
		return false
	}
	want := "T"
	if strings.HasPrefix(fn.Name.Name, "Benchmark") {
		want = "B"
	} else if strings.HasPrefix(fn.Name.Name, "Fuzz") {
		want = "F"
	}
	return selector.Sel.Name == want
}

func collectTestDescriptors(units []sourceUnit, edges []impactEdge, files []parsedGoFile) []testDescriptor {
	descriptorsByID := make(map[string]testDescriptor)
	for _, pf := range files {
		if !strings.HasSuffix(pf.path, "_test.go") {
			continue
		}
		for _, decl := range pf.tree.Decls {
			fn, ok := decl.(*ast.FuncDecl)
			if ok && fn.Recv == nil && isGoTestFunc(fn, pf.imports) {
				test := testDescriptor{"golang:" + pf.importPath + ":" + fn.Name.Name, pf.importPath + "." + fn.Name.Name, "go-testing"}
				descriptorsByID[test.Identity] = test
			}
		}
	}
	descriptors := make([]testDescriptor, 0, len(descriptorsByID))
	for _, descriptor := range descriptorsByID {
		descriptors = append(descriptors, descriptor)
	}
	sort.Slice(descriptors, func(i, j int) bool { return descriptors[i].Identity < descriptors[j].Identity })
	return descriptors
}

func memberForFunc(pf parsedGoFile, fn *ast.FuncDecl) string {
	return funcIdentity(pf, fn)
}

func funcIdentity(pf parsedGoFile, fn *ast.FuncDecl) string {
	prefix := pf.importPath + "/"
	if fn.Recv != nil && len(fn.Recv.List) != 0 {
		prefix += receiverName(fn.Recv.List[0].Type) + "/"
	}
	return "golang:member:" + prefix + fn.Name.Name + "(" + funcParams(fn.Type) + ")"
}

func receiverName(expr ast.Expr) string {
	switch e := expr.(type) {
	case *ast.StarExpr:
		return receiverName(e.X)
	case *ast.Ident:
		return e.Name
	case *ast.IndexExpr:
		return receiverName(e.X)
	case *ast.IndexListExpr:
		return receiverName(e.X)
	}
	return "Receiver"
}

func funcParams(fn *ast.FuncType) string {
	if fn == nil || fn.Params == nil {
		return ""
	}
	params := make([]string, 0)
	for _, field := range fn.Params.List {
		typ := canonicalType(field.Type)
		count := len(field.Names)
		if count == 0 {
			count = 1
		}
		for i := 0; i < count; i++ {
			params = append(params, typ)
		}
	}
	return strings.Join(params, ",")
}

func canonicalType(expr ast.Expr) string {
	var buf bytes.Buffer
	if err := format.Node(&buf, token.NewFileSet(), expr); err == nil {
		return strings.Join(strings.Fields(buf.String()), "")
	}
	return "?"
}

func nodeSignature(node ast.Node) string {
	var buf bytes.Buffer
	if err := format.Node(&buf, token.NewFileSet(), node); err != nil {
		return "?"
	}
	h := sha256.Sum256(buf.Bytes())
	return hex.EncodeToString(h[:])
}

func isGeneratedGo(content []byte) bool {
	limit := len(content)
	if limit > 4096 {
		limit = 4096
	}
	lower := strings.ToLower(string(content[:limit]))
	return strings.Contains(lower, "code generated") && strings.Contains(lower, "do not edit")
}

func hasBuildTag(content []byte) bool {
	text := string(content)
	return strings.Contains(text, "//go:build") || strings.Contains(text, "// +build")
}

func indexCalls(pf parsedGoFile, table symbolTable, edges *[]impactEdge, warnings *[]string) {
	for _, decl := range pf.tree.Decls {
		fn, ok := decl.(*ast.FuncDecl)
		if !ok || fn.Body == nil {
			continue
		}
		caller := memberForFunc(pf, fn)
		localTypes := map[string]string{}
		if fn.Recv != nil {
			for _, field := range fn.Recv.List {
				for _, name := range field.Names {
					localTypes[name.Name] = receiverName(field.Type)
				}
			}
		}
		if fn.Type.Params != nil {
			for _, field := range fn.Type.Params.List {
				for _, name := range field.Names {
					if externalType := importedTypeName(field.Type, pf.imports); externalType != "" {
						localTypes[name.Name] = "external:" + externalType
						continue
					}
					if ident, ok := field.Type.(*ast.Ident); ok {
						localTypes[name.Name] = ident.Name
					}
				}
			}
		}
		ast.Inspect(fn.Body, func(node ast.Node) bool {
			if decl, ok := node.(*ast.DeclStmt); ok {
				if gen, ok := decl.Decl.(*ast.GenDecl); ok {
					for _, spec := range gen.Specs {
						if value, ok := spec.(*ast.ValueSpec); ok && value.Type != nil {
							if ident, ok := value.Type.(*ast.Ident); ok {
								for _, name := range value.Names {
									localTypes[name.Name] = ident.Name
								}
							}
						}
					}
				}
			}
			call, ok := node.(*ast.CallExpr)
			if !ok {
				return true
			}
			switch fun := call.Fun.(type) {
			case *ast.Ident:
				if target, ok := table.functions[pf.importPath+"/"+fun.Name]; ok {
					*edges = append(*edges, impactEdge{caller, target, "staticDependency"})
				} else if !goBuiltin(fun.Name) {
					*warnings = append(*warnings, "Unresolved call in "+pf.path+": "+fun.Name)
				}
			case *ast.SelectorExpr:
				if base, ok := fun.X.(*ast.Ident); ok {
					if imported, ok := pf.imports[base.Name]; ok {
						if target, found := table.functions[imported+"/"+fun.Sel.Name]; found {
							*edges = append(*edges, impactEdge{caller, target, "staticDependency"})
						} else {
							*warnings = append(*warnings, "Unresolved imported call in "+pf.path+": "+imported+"."+fun.Sel.Name)
						}
					} else if typ, ok := localTypes[base.Name]; ok {
						if strings.HasPrefix(typ, "external:") {
							return true
						}
						if target, found := table.methods[pf.importPath+"/"+typ+"/"+fun.Sel.Name]; found {
							*edges = append(*edges, impactEdge{caller, target, "staticDependency"})
						} else {
							*warnings = append(*warnings, "Unresolved receiver call in "+pf.path+": "+typ+"."+fun.Sel.Name)
						}
					} else {
						*warnings = append(*warnings, "Unresolved receiver call in "+pf.path+": "+fun.Sel.Name)
					}
				}
			default:
				*warnings = append(*warnings, "Dynamic call in "+pf.path)
			}
			return true
		})
	}
}

func importedTypeName(expr ast.Expr, imports map[string]string) string {
	if star, ok := expr.(*ast.StarExpr); ok {
		expr = star.X
	}
	selector, ok := expr.(*ast.SelectorExpr)
	if !ok {
		return ""
	}
	packageName, ok := selector.X.(*ast.Ident)
	if !ok {
		return ""
	}
	imported, ok := imports[packageName.Name]
	if !ok {
		return ""
	}
	return imported + "." + selector.Sel.Name
}

func goBuiltin(name string) bool {
	switch name {
	case "append", "cap", "clear", "close", "complex", "copy", "delete", "imag", "len", "make", "max", "min", "new", "panic", "print", "println", "real", "recover":
		return true
	default:
		return false
	}
}

func uniqueUnits(units []sourceUnit) []sourceUnit {
	seen := map[string]bool{}
	result := make([]sourceUnit, 0, len(units))
	for _, unit := range units {
		if !seen[unit.Identity] {
			seen[unit.Identity] = true
			result = append(result, unit)
		}
	}
	return result
}

func uniqueEdges(edges []impactEdge) []impactEdge {
	seen := map[string]bool{}
	result := make([]impactEdge, 0, len(edges))
	for _, edge := range edges {
		key := edge.SourceIdentity + "\x00" + edge.TargetIdentity + "\x00" + edge.Kind
		if !seen[key] {
			seen[key] = true
			result = append(result, edge)
		}
	}
	return result
}

func sortIndex(units *[]sourceUnit, edges *[]impactEdge, warnings *[]string) {
	sort.Slice(*units, func(i, j int) bool { return (*units)[i].Identity < (*units)[j].Identity })
	sort.Slice(*edges, func(i, j int) bool { return edgeKey((*edges)[i]) < edgeKey((*edges)[j]) })
	*warnings = sortStringsUnique(*warnings)
}

func edgeKey(edge impactEdge) string {
	return edge.SourceIdentity + "\x00" + edge.TargetIdentity + "\x00" + edge.Kind
}

func detectGo(snapshot repositorySnapshot) detectedLanguage {
	count := 0
	evidence := make([]detectionEvidence, 0)
	for _, file := range snapshot.Files {
		p := normalizePath(file.Path)
		if strings.HasSuffix(p, ".go") || path.Base(p) == "go.mod" || path.Base(p) == "go.work" {
			count++
		}
	}
	if count == 0 {
		return detectedLanguage{"golang", "none", evidence}
	}
	evidence = append(evidence, detectionEvidence{"extension", ".go", count})
	return detectedLanguage{"golang", "high", evidence}
}
