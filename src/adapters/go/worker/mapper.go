package main

import (
	"path"
	"sort"
	"strings"
)

const mapVisitLimit = 10000

type mapNode struct {
	id   string
	path []string
}

func mapGo(input mapRequest) mappingResult {
	index := input.Index
	tests := map[string]testDescriptor{}
	for _, test := range index.Tests {
		tests[test.Identity] = test
	}
	forward := map[string][]impactEdge{}
	reverseStatic := map[string][]impactEdge{}
	containmentParents := map[string][]string{}
	for _, edge := range index.Edges {
		forward[edge.SourceIdentity] = append(forward[edge.SourceIdentity], edge)
		kind := canonicalEdgeKind(edge.Kind)
		if kind == "staticdependency" {
			reverseStatic[edge.TargetIdentity] = append(reverseStatic[edge.TargetIdentity], edge)
		}
		if kind == "containment" {
			containmentParents[edge.SourceIdentity] = append(containmentParents[edge.SourceIdentity], edge.TargetIdentity)
		}
	}
	for key := range forward {
		sort.Slice(forward[key], func(i, j int) bool { return edgeKey(forward[key][i]) < edgeKey(forward[key][j]) })
	}
	for key := range reverseStatic {
		sort.Slice(reverseStatic[key], func(i, j int) bool { return edgeKey(reverseStatic[key][i]) < edgeKey(reverseStatic[key][j]) })
	}
	for key := range containmentParents {
		sort.Strings(containmentParents[key])
	}

	selected := map[string]requestedTest{}
	unmapped := make([]changedUnit, 0)
	warnings := make([]string, 0)
	visits := 0
	for _, changed := range input.ChangedUnits {
		if changed.Identity == "" {
			unmapped = append(unmapped, changedUnit{changed.Identity, changed.Kind, changed.ChangeKind, false})
			continue
		}
		found := false
		// A changed test is always directly requested, even if its implementation has no callers.
		if test, ok := tests[changed.Identity]; ok {
			addMappedTest(selected, test, impactReason{"staticDependency", changed.Identity, []string{changed.Identity, test.Identity}})
			found = true
		}
		queue := []mapNode{{changed.Identity, []string{changed.Identity}}}
		seen := map[string]bool{changed.Identity: true}
		for len(queue) != 0 && visits < mapVisitLimit {
			current := queue[0]
			queue = queue[1:]
			visits++
			for _, edge := range forward[current.id] {
				if test, ok := tests[edge.TargetIdentity]; ok {
					p := appendCopy(current.path, test.Identity)
					addMappedTest(selected, test, impactReason{"staticDependency", changed.Identity, p})
					found = true
				}
			}
			for _, edge := range reverseStatic[current.id] {
				if seen[edge.SourceIdentity] {
					continue
				}
				seen[edge.SourceIdentity] = true
				queue = append(queue, mapNode{edge.SourceIdentity, appendCopy(current.path, edge.SourceIdentity)})
			}
		}
		if visits >= mapVisitLimit {
			warnings = append(warnings, "Mapping traversal reached its internal limit.")
		}
		if !found {
			found = mapAncestorFallback(changed, input, tests, containmentParents, selected)
		}
		if !found {
			unmapped = append(unmapped, changedUnit{changed.Identity, changed.Kind, changed.ChangeKind, false})
		}
	}

	requested := make([]requestedTest, 0, len(selected))
	for _, test := range selected {
		sort.Slice(test.Reasons, func(i, j int) bool { return reasonKey(test.Reasons[i]) < reasonKey(test.Reasons[j]) })
		requested = append(requested, test)
	}
	sort.Slice(requested, func(i, j int) bool { return requested[i].Identity < requested[j].Identity })
	sort.Slice(unmapped, func(i, j int) bool { return unmapped[i].Identity < unmapped[j].Identity })
	return mappingResult{requested, unmapped, sortStringsUnique(warnings)}
}

func addMappedTest(selected map[string]requestedTest, test testDescriptor, reason impactReason) {
	entry, exists := selected[test.Identity]
	if !exists {
		selected[test.Identity] = requestedTest{test.Identity, test.DisplayName, test.Framework, []impactReason{reason}, true}
		return
	}
	for _, old := range entry.Reasons {
		if reasonKey(old) == reasonKey(reason) {
			return
		}
	}
	entry.Reasons = append(entry.Reasons, reason)
	selected[test.Identity] = entry
}

func mapAncestorFallback(changed changedUnit, input mapRequest, tests map[string]testDescriptor, parents map[string][]string, selected map[string]requestedTest) bool {
	selectedBefore := len(selected)
	ancestors := []string{}
	if strings.HasPrefix(changed.Identity, "golang:project:") {
		manifest := strings.TrimPrefix(changed.Identity, "golang:project:")
		if path.Base(manifest) == "go.work" || path.Base(manifest) == "go.work.sum" {
			ancestors = append(ancestors, "workspace")
		} else {
			for _, test := range tests {
				if testBelongsToManifest(test.Identity, manifest, input.Snapshot.Files) {
					addMappedTest(selected, test, impactReason{"ancestorFallback", changed.Identity, []string{changed.Identity, test.Identity}})
				}
			}
		}
	} else if strings.HasPrefix(changed.Identity, "golang:package:") {
		ancestors = append(ancestors, strings.TrimPrefix(changed.Identity, "golang:package:"))
	} else {
		for _, parent := range parents[changed.Identity] {
			if strings.HasPrefix(parent, "golang:package:") {
				ancestors = append(ancestors, strings.TrimPrefix(parent, "golang:package:"))
			}
		}
		if len(ancestors) == 0 && strings.HasPrefix(changed.Identity, "golang:file:") {
			p := strings.TrimPrefix(changed.Identity, "golang:file:")
			// Package containment is keyed by the file identity in the index.
			for _, parent := range parents[changed.Identity] {
				if strings.HasPrefix(parent, "golang:package:") {
					ancestors = append(ancestors, strings.TrimPrefix(parent, "golang:package:"))
				}
			}
			_ = p
		}
	}
	if len(ancestors) == 0 {
		if strings.HasPrefix(changed.Identity, "golang:project:") && strings.HasSuffix(changed.Identity, "go.sum") {
			manifest := strings.TrimPrefix(changed.Identity, "golang:project:")
			root := path.Dir(manifest)
			for _, test := range tests {
				if testInRoot(test.Identity, root, input.Snapshot.Files) {
					addMappedTest(selected, test, impactReason{"ancestorFallback", changed.Identity, []string{changed.Identity, test.Identity}})
				}
			}
		}
		return len(selected) > selectedBefore
	}
	if len(ancestors) == 1 && ancestors[0] == "workspace" {
		workspacePath := strings.TrimPrefix(changed.Identity, "golang:project:")
		if path.Base(workspacePath) == "go.work.sum" {
			workspacePath = path.Join(path.Dir(workspacePath), "go.work")
		}
		roots := workspaceRootsForFiles(input.Snapshot.Files, normalizePath(workspacePath))
		found := false
	forTest:
		for _, test := range tests {
			if len(roots) != 0 {
				for _, root := range roots {
					if testInRoot(test.Identity, root, input.Snapshot.Files) {
						addMappedTest(selected, test, impactReason{"ancestorFallback", changed.Identity, []string{changed.Identity, test.Identity}})
						found = true
						continue forTest
					}
				}
				continue
			}
			addMappedTest(selected, test, impactReason{"ancestorFallback", changed.Identity, []string{changed.Identity, test.Identity}})
			found = true
		}
		return found
	}
	found := false
	for _, test := range tests {
		for _, pkg := range ancestors {
			if testPackage(test.Identity) == pkg {
				addMappedTest(selected, test, impactReason{"ancestorFallback", changed.Identity, []string{changed.Identity, "golang:package:" + pkg, test.Identity}})
				found = true
			}
		}
	}
	return found
}

func workspaceUseRoots(files []snapshotFile, requested string) []string {
	for _, file := range files {
		if normalizePath(file.Path) != requested {
			continue
		}
		workPath := normalizePath(file.Path)
		base := path.Dir(workPath)
		inBlock := false
		roots := []string{}
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
			}
			if strings.HasPrefix(line, "use ") && strings.HasSuffix(line, ")") == false && !inBlock {
				value = strings.TrimSpace(strings.TrimPrefix(line, "use "))
			}
			if value == "" || strings.HasPrefix(value, "//") {
				continue
			}
			value = strings.Trim(value, "\"")
			root := normalizePath(path.Join(base, value))
			if root == "." {
				root = ""
			}
			roots = append(roots, root)
		}
		return sortStringsUnique(roots)
	}
	return nil
}

func testBelongsToManifest(testID, manifest string, files []snapshotFile) bool {
	if path.Base(manifest) == "go.work" {
		return true
	}
	root := path.Dir(manifest)
	return testInRoot(testID, root, files)
}

func testInRoot(testID, root string, files []snapshotFile) bool {
	pkg := testPackage(testID)
	root = strings.TrimPrefix(normalizePath(root), "./")
	if root == "." {
		root = ""
	}
	for _, file := range files {
		p := normalizePath(file.Path)
		manifest := "go.mod"
		if root != "" {
			manifest = root + "/go.mod"
		}
		if p == manifest {
			for _, line := range strings.Split(string(file.Content), "\n") {
				fields := strings.Fields(strings.TrimSpace(strings.SplitN(line, "//", 2)[0]))
				if len(fields) >= 2 && fields[0] == "module" {
					return pkg == fields[1] || strings.HasPrefix(pkg, fields[1]+"/")
				}
			}
		}
	}
	return false
}

func testPackage(testID string) string {
	testID = strings.TrimPrefix(testID, "golang:")
	if i := strings.LastIndex(testID, ":"); i >= 0 {
		return testID[:i]
	}
	return testID
}

func appendCopy(values []string, value string) []string {
	result := make([]string, len(values), len(values)+1)
	copy(result, values)
	return append(result, value)
}

func reasonKey(reason impactReason) string {
	return reason.Kind + "\x00" + reason.ChangedUnit + "\x00" + strings.Join(reason.Path, "\x00")
}

func canonicalEdgeKind(kind string) string {
	return strings.NewReplacer("_", "", "-", "", " ", "").Replace(strings.ToLower(kind))
}
