using System.Globalization;
using System.Text;
using System.Text.Json;
using Merkle.Core.Adapters;
using Merkle.Core.Errors;
using Merkle.Core.History;
using Merkle.Core.Reporting;
using Merkle.Core.State;
using Microsoft.Data.Sqlite;

namespace Merkle.Infrastructure.State;

/// <summary>
/// Repository-local SQLite state. The small public seam deliberately hides files, migrations,
/// journaling, and SQLite retry semantics from callers.
/// </summary>
public sealed class LocalStateStore : IStateStore, IStatePublicationStore, IIndexStore, IHistoryStore
{
    private const string MarkerName = ".merkle-state";
    private const int SchemaVersion = 2;
    private const int MaxIdentifierLength = 512;
    private const int MaxEvidenceEntries = 100_000;
    private readonly string _repositoryRoot;
    private readonly string _statePath;
    private readonly string _databasePath;
    private readonly string _repositoryIdentity;
    private readonly int _maxReportBytes;
    private readonly SecretRedactor _redactor;

    static LocalStateStore() => SQLitePCL.Batteries_V2.Init();

    public LocalStateStore(
        string repositoryRoot,
        string stateDirectory,
        string repositoryIdentity,
        int maxReportBytes = 16_777_216,
        SecretRedactor? redactor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        _repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        _statePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(stateDirectory) ? stateDirectory : Path.Combine(_repositoryRoot, stateDirectory)));
        _databasePath = Path.Combine(_statePath, "state.db");
        _repositoryIdentity = repositoryIdentity;
        _maxReportBytes = maxReportBytes > 0 ? maxReportBytes : throw new ArgumentOutOfRangeException(nameof(maxReportBytes));
        _redactor = redactor ?? SecretRedactor.Default;
        ValidateStatePath();
    }

    public string StatePath => _statePath;

    public async ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRunId(runId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var runsPath = EnsureOwnedSubdirectory("runs");
        var journalPath = Path.Combine(runsPath, runId);
        if (Directory.Exists(journalPath) || File.Exists(journalPath))
        {
            throw new ConfigurationException("DuplicateRunIdentity", "A run journal with this identity already exists.");
        }

        Directory.CreateDirectory(journalPath);
        RejectReparsePoint(journalPath);
        return new RunJournal(runId, journalPath);
    }

    public ValueTask PublishAsync(RunJournal journal, TerminalReport report, CancellationToken cancellationToken) =>
        PublishAsync(journal, new StatePublication(report), cancellationToken);

    public async ValueTask PublishAsync(RunJournal journal, StatePublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(publication.TerminalReport);
        var report = publication.TerminalReport;
        ValidateRunId(journal.RunId);
        if (!StringComparer.Ordinal.Equals(journal.RunId, report.RunId))
        {
            throw new ConfigurationException("RunIdentityMismatch", "The report does not belong to the supplied run journal.");
        }

        ValidatePublication(publication);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        ValidateJournal(journal);
        var json = _redactor.Redact(JsonSerializer.Serialize(report, MerkleJsonContext.Default.TerminalReport));
        if (Encoding.UTF8.GetByteCount(json) > _maxReportBytes)
        {
            throw new AnalysisException("ReportSizeLimitExceeded", "The terminal report exceeds the configured byte limit.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO terminal_runs(run_id, report_json, published_at) VALUES ($run, $report, $published);",
            cancellationToken, ("$run", report.RunId), ("$report", json), ("$published", report.EvidenceCutoff.ToUnixTimeMilliseconds())).ConfigureAwait(false);

        foreach (var item in publication.PersistedIndexes)
        {
            await InsertIndexAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        }

        foreach (var history in publication.PersistedHistoryRuns)
        {
            await InsertHistoryAsync(connection, transaction, report.RunId, history, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction,
            "INSERT INTO current_state(singleton, run_id) VALUES (1, $run) ON CONFLICT(singleton) DO UPDATE SET run_id = excluded.run_id;",
            cancellationToken, ("$run", report.RunId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Kept as a redacted, immutable diagnostics artifact; readers use the transactional pointer.
        var reportsPath = EnsureOwnedSubdirectory("reports");
        var artifact = Path.Combine(reportsPath, $"{report.RunId}.json");
        if (!File.Exists(artifact))
        {
            await WriteDurablyAsync(artifact, json, cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.EnumerateFileSystemEntries(journal.JournalPath).Any())
        {
            Directory.Delete(journal.JournalPath, recursive: false);
        }
    }

    public async ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_statePath)) return null;
        ValidateMarker();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.report_json FROM current_state c JOIN terminal_runs t ON t.run_id = c.run_id WHERE c.singleton = 1;";
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value) return null;
        var report = JsonSerializer.Deserialize(value, MerkleJsonContext.Default.TerminalReport) ?? throw new AnalysisException("CorruptState", "The current terminal report is empty.");
        if (!IsCompatible(report)) throw new AnalysisException("IncompatibleReport", "The current terminal report is incompatible with this repository or schema.");
        return report;
    }

    public async ValueTask<AdapterIndex?> ReadIndexAsync(IndexCompatibilityKey compatibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ValidateIndexKey(compatibility);
        if (!Directory.Exists(_statePath)) return null;
        ValidateMarker();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT index_json FROM adapter_indexes WHERE repository_identity=$repo AND snapshot_identity=$snapshot AND index_schema=$schema AND hash_algorithm=$hash AND semantic_normalization=$semantic AND adapter_identity=$adapter AND adapter_protocol=$protocol AND unit_identity=$unit AND test_identity=$test AND language=$language AND solution_digest=$solution;";
        Add(command, "$repo", compatibility.RepositoryIdentity); Add(command, "$snapshot", compatibility.SnapshotIdentity); Add(command, "$schema", compatibility.IndexSchema);
        Add(command, "$hash", compatibility.HashAlgorithmVersion); Add(command, "$semantic", compatibility.SemanticNormalizationVersion); Add(command, "$adapter", compatibility.AdapterIdentity);
        Add(command, "$protocol", compatibility.AdapterProtocolVersion); Add(command, "$unit", compatibility.UnitIdentityVersion); Add(command, "$test", compatibility.TestIdentityVersion);
        Add(command, "$language", compatibility.Language); Add(command, "$solution", compatibility.SolutionBuildDigest ?? string.Empty);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string json ? null : JsonSerializer.Deserialize(json, StateJsonContext.Default.AdapterIndex)
            ?? throw new AnalysisException("CorruptState", "The stored adapter index is empty.");
    }

    public async ValueTask<IReadOnlyList<HistoricalRun>> ReadHistoryAsync(HistoryCompatibilityKey compatibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ValidateHistoryKey(compatibility);
        if (!Directory.Exists(_statePath)) return [];
        ValidateMarker();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT history_json FROM history_runs WHERE repository_identity=$repo AND schema_version=$schema AND adapter_identity=$adapter AND build_fingerprint_family=$build ORDER BY completed_at;";
        Add(command, "$repo", compatibility.RepositoryIdentity); Add(command, "$schema", compatibility.SchemaVersion); Add(command, "$adapter", compatibility.AdapterIdentity); Add(command, "$build", compatibility.BuildFingerprintFamily);
        var result = new List<HistoricalRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(JsonSerializer.Deserialize(reader.GetString(0), StateJsonContext.Default.HistoricalRun)
                ?? throw new AnalysisException("CorruptState", "A stored history run is empty."));
        }

        return result;
    }

    public async ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_statePath)) return new StateStatus("sqlite", SchemaVersion, 0, null, true);
        var size = CalculateOwnedSize(_statePath);
        if (!File.Exists(Path.Combine(_statePath, MarkerName))) return new StateStatus("unrecognized-local-directory", 0, size, null, true);
        try { ValidateMarker(); }
        catch (ConfigurationException) { return new StateStatus("sqlite", 0, size, null, true); }

        try
        {
            var current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current is null) return new StateStatus("sqlite", SchemaVersion, size, null, true);
            var hasIndex = await HasCompatibleIndexAsync(current, cancellationToken).ConfigureAwait(false);
            return new StateStatus("sqlite", SchemaVersion, size, current.RunId, !hasIndex);
        }
        catch (MerkleException) { return new StateStatus("sqlite", SchemaVersion, size, null, true); }
        catch (JsonException) { return new StateStatus("sqlite", SchemaVersion, size, null, true); }
        catch (SqliteException) { return new StateStatus("sqlite", SchemaVersion, size, null, true); }
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_statePath)) return ValueTask.CompletedTask;
        ValidateMarker();
        DeleteOwnedTree(_statePath);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> HasCompatibleIndexAsync(TerminalReport report, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM adapter_indexes WHERE repository_identity=$repo AND snapshot_identity=$snapshot AND index_schema=$schema);";
        Add(command, "$repo", _repositoryIdentity); Add(command, "$snapshot", report.Candidate.Value); Add(command, "$schema", report.IndexSchema);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ValidateStatePath();
        var marker = Path.Combine(_statePath, MarkerName);
        if (Directory.Exists(_statePath))
        {
            if (!File.Exists(marker)) throw new ConfigurationException("InvalidStateDirectory", "The configured state directory already exists without a Merkle ownership marker.");
            ValidateMarker();
        }
        else
        {
            var parent = Path.GetDirectoryName(_statePath) ?? throw new ConfigurationException("StateParentUnavailable", "The parent of the local state directory must already exist.");
            if (!Directory.Exists(parent)) throw new ConfigurationException("StateParentUnavailable", "The parent of the local state directory must already exist.");
            var staging = Path.Combine(parent, $".merkle-init-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(staging, MarkerName), $"{SchemaVersion}\n{_repositoryIdentity}\n", Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                Directory.Move(staging, _statePath);
            }
            catch (IOException error)
            {
                DeleteOwnedTree(staging);
                throw new ConfigurationException("InvalidStateDirectory", "The state directory was created concurrently and was not claimed.", error);
            }
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ValidateStatePath();
        var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, DefaultTimeout = 5 };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async ValueTask InitializeSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS terminal_runs(run_id TEXT NOT NULL PRIMARY KEY, report_json TEXT NOT NULL, published_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS current_state(singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1), run_id TEXT NOT NULL REFERENCES terminal_runs(run_id));
            CREATE TABLE IF NOT EXISTS adapter_indexes(
              repository_identity TEXT NOT NULL, snapshot_identity TEXT NOT NULL, index_schema INTEGER NOT NULL,
              hash_algorithm TEXT NOT NULL, semantic_normalization TEXT NOT NULL, adapter_identity TEXT NOT NULL,
              adapter_protocol TEXT NOT NULL, unit_identity TEXT NOT NULL, test_identity TEXT NOT NULL, language TEXT NOT NULL,
              solution_digest TEXT NOT NULL, index_json TEXT NOT NULL,
              PRIMARY KEY(repository_identity,snapshot_identity,index_schema,hash_algorithm,semantic_normalization,adapter_identity,adapter_protocol,unit_identity,test_identity,language,solution_digest));
            CREATE TABLE IF NOT EXISTS history_runs(
              id INTEGER PRIMARY KEY AUTOINCREMENT, terminal_run_id TEXT NOT NULL REFERENCES terminal_runs(run_id),
              repository_identity TEXT NOT NULL, schema_version TEXT NOT NULL, adapter_identity TEXT NOT NULL,
              build_fingerprint_family TEXT NOT NULL, completed_at INTEGER NOT NULL, history_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history_compatibility ON history_runs(repository_identity,schema_version,adapter_identity,build_fingerprint_family,completed_at);
            INSERT OR IGNORE INTO schema_migrations(version) VALUES (2);
            """;
        await ExecuteAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var version = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version != SchemaVersion) throw new ConfigurationException("IncompatibleState", "The local SQLite schema is not compatible with this version of Merkle.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertIndexAsync(SqliteConnection connection, SqliteTransaction transaction, PersistedAdapterIndex item, CancellationToken cancellationToken)
    {
        var key = item.Compatibility;
        var json = JsonSerializer.Serialize(item.Index, StateJsonContext.Default.AdapterIndex);
        await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO adapter_indexes(repository_identity,snapshot_identity,index_schema,hash_algorithm,semantic_normalization,adapter_identity,adapter_protocol,unit_identity,test_identity,language,solution_digest,index_json) VALUES($repo,$snapshot,$schema,$hash,$semantic,$adapter,$protocol,$unit,$test,$language,$solution,$json);", cancellationToken,
            ("$repo", key.RepositoryIdentity), ("$snapshot", key.SnapshotIdentity), ("$schema", key.IndexSchema), ("$hash", key.HashAlgorithmVersion), ("$semantic", key.SemanticNormalizationVersion), ("$adapter", key.AdapterIdentity), ("$protocol", key.AdapterProtocolVersion), ("$unit", key.UnitIdentityVersion), ("$test", key.TestIdentityVersion), ("$language", key.Language), ("$solution", key.SolutionBuildDigest ?? string.Empty), ("$json", json)).ConfigureAwait(false);
    }

    private static async ValueTask InsertHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, string terminalRunId, HistoricalRun history, CancellationToken cancellationToken)
    {
        var key = history.Compatibility;
        var json = JsonSerializer.Serialize(history, StateJsonContext.Default.HistoricalRun);
        await ExecuteAsync(connection, transaction, "INSERT INTO history_runs(terminal_run_id,repository_identity,schema_version,adapter_identity,build_fingerprint_family,completed_at,history_json) VALUES($run,$repo,$schema,$adapter,$build,$completed,$json);", cancellationToken,
            ("$run", terminalRunId), ("$repo", key.RepositoryIdentity), ("$schema", key.SchemaVersion), ("$adapter", key.AdapterIdentity), ("$build", key.BuildFingerprintFamily), ("$completed", history.CompletedAt.ToUnixTimeMilliseconds()), ("$json", json)).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (Name, Value) in parameters) Add(command, Name, Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private void ValidatePublication(StatePublication publication)
    {
        if (!IsCompatible(publication.TerminalReport) || publication.TerminalReport.TerminalStatus == Merkle.Core.Domain.TerminalStatus.Interrupted)
        {
            throw new AnalysisException("IncompleteEvidence", "Only a compatible, completed terminal report can be published.");
        }
        if (publication.PersistedIndexes.Count > MaxEvidenceEntries || publication.PersistedHistoryRuns.Count > MaxEvidenceEntries) throw new AnalysisException("EvidenceLimitExceeded", "The publication contains too many evidence records.");
        foreach (var item in publication.PersistedIndexes)
        {
            ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(item.Compatibility); ArgumentNullException.ThrowIfNull(item.Index);
            ValidateIndexKey(item.Compatibility);
            if (item.Index.Units is null || item.Index.Edges is null || item.Index.Tests is null || item.Index.Units.Count > MaxEvidenceEntries || item.Index.Edges.Count > MaxEvidenceEntries || item.Index.Tests.Count > MaxEvidenceEntries || item.Index.Units.Any(unit => !IsBounded(unit.Identity) || !IsBounded(unit.Path) || !IsBounded(unit.ContentHash) || !IsBounded(unit.SemanticSignature)) || item.Index.Tests.Any(test => !IsBounded(test.Identity) || !IsBounded(test.DisplayName) || !IsBounded(test.Framework)) || item.Index.Edges.Any(edge => !IsBounded(edge.SourceIdentity) || !IsBounded(edge.TargetIdentity))) throw new AnalysisException("IncompleteEvidence", "An adapter index is structurally incomplete or exceeds store limits.");
        }

        foreach (var history in publication.PersistedHistoryRuns)
        {
            ArgumentNullException.ThrowIfNull(history); ValidateHistoryKey(history.Compatibility);
            if (history.Status is not (HistoryRunStatus.Succeeded or HistoryRunStatus.Failed) || history.ChangedUnitIdentities.Count > MaxEvidenceEntries || history.Tests.Count > MaxEvidenceEntries || history.ChangedUnitIdentities.Any(identity => !IsBounded(identity)) || history.Tests.Any(test => !IsBounded(test.TestIdentity) || test.ObservedUnitIdentities.Count > MaxEvidenceEntries || test.ObservedUnitIdentities.Any(identity => !IsBounded(identity)))) throw new AnalysisException("IncompleteEvidence", "Interrupted or structurally incomplete history cannot be published.");
        }
    }

    private static void ValidateIndexKey(IndexCompatibilityKey key)
    {
        if (key.IndexSchema < 1 || !IsBounded(key.RepositoryIdentity) || !IsBounded(key.SnapshotIdentity) || !IsBounded(key.HashAlgorithmVersion) || !IsBounded(key.SemanticNormalizationVersion) || !IsBounded(key.AdapterIdentity) || !IsBounded(key.AdapterProtocolVersion) || !IsBounded(key.UnitIdentityVersion) || !IsBounded(key.TestIdentityVersion) || !IsBounded(key.Language) || (key.SolutionBuildDigest is not null && !IsBounded(key.SolutionBuildDigest))) throw new ConfigurationException("InvalidCompatibilityKey", "The adapter index compatibility key is incomplete or too large.");
    }

    private static void ValidateHistoryKey(HistoryCompatibilityKey key)
    {
        if (!IsBounded(key.RepositoryIdentity) || !IsBounded(key.SchemaVersion) || !IsBounded(key.AdapterIdentity) || !IsBounded(key.BuildFingerprintFamily)) throw new ConfigurationException("InvalidCompatibilityKey", "The history compatibility key is incomplete or too large.");
    }

    private bool IsCompatible(TerminalReport report) => report.SchemaVersion == 1 && StringComparer.Ordinal.Equals(report.RepositoryIdentity, _repositoryIdentity) && report.IndexSchema == Merkle.Core.Indexing.MerkleIndex.SchemaVersion && report.IdentitySchemas.Contains("unit:1", StringComparer.Ordinal) && report.IdentitySchemas.Contains("test:1", StringComparer.Ordinal) && report.Adapters.All(adapter => StringComparer.Ordinal.Equals(adapter.ProtocolVersion, "1.0") && StringComparer.Ordinal.Equals(adapter.UnitIdentityVersion, "1") && StringComparer.Ordinal.Equals(adapter.TestIdentityVersion, "1"));

    private void ValidateStatePath()
    {
        var prefix = _repositoryRoot + Path.DirectorySeparatorChar;
        if (StringComparer.Ordinal.Equals(_statePath, _repositoryRoot) || !_statePath.StartsWith(prefix, StringComparison.Ordinal)) throw new ConfigurationException("UnsafeStatePath", "The local state directory must be a dedicated subdirectory of the repository.");
        var current = _repositoryRoot;
        foreach (var segment in Path.GetRelativePath(_repositoryRoot, _statePath).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ConfigurationException("UnsafeStatePath", "The local state path cannot traverse a symbolic link.");
        }
    }

    private void ValidateMarker()
    {
        var marker = Path.Combine(_statePath, MarkerName);
        if (!File.Exists(marker)) throw new ConfigurationException("InvalidStateDirectory", "The state directory has no Merkle ownership marker.");
        var lines = File.ReadAllLines(marker);
        if (lines.Length < 2 || lines[0] != SchemaVersion.ToString(CultureInfo.InvariantCulture) || !StringComparer.Ordinal.Equals(lines[1], _repositoryIdentity)) throw new ConfigurationException("IncompatibleState", "The state directory belongs to another repository or schema.");
    }

    private void ValidateJournal(RunJournal journal)
    {
        var expected = Path.GetFullPath(Path.Combine(_statePath, "runs", journal.RunId));
        if (!StringComparer.Ordinal.Equals(expected, Path.GetFullPath(journal.JournalPath))) throw new ConfigurationException("InvalidRunJournal", "The run journal is outside this state store.");
        RejectReparsePoint(Path.Combine(_statePath, "runs")); RejectReparsePoint(journal.JournalPath);
    }

    private string EnsureOwnedSubdirectory(string name)
    {
        var path = Path.Combine(_statePath, name);
        if (File.Exists(path) && !Directory.Exists(path)) throw new ConfigurationException("UnsafeStatePath", $"State path '{name}' is not a directory.");
        Directory.CreateDirectory(path); RejectReparsePoint(path); return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ConfigurationException("UnsafeStatePath", "The state store cannot traverse a symbolic link.");
    }

    private static void DeleteOwnedTree(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path, recursive: false); return; }
        foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(path)) DeleteOwnedTree(directory);
        Directory.Delete(path, recursive: false);
    }

    private static long CalculateOwnedSize(string path)
    {
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(path)) size = checked(size + new FileInfo(file).Length);
        foreach (var directory in Directory.EnumerateDirectories(path)) if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) size = checked(size + CalculateOwnedSize(directory));
        return size;
    }

    private static bool IsBounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentifierLength;

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128 || runId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new ConfigurationException("InvalidRunIdentity", "Run identities may contain only ASCII letters, digits, '-' and '_'.");
    }

    private static async ValueTask WriteDurablyAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
