using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace LesTool.Platform.Storage;

public enum SettingsReadState { NewFile, Valid, Recovered, ProtectedDefaults }
public sealed record SaveResult(bool Succeeded, long PersistedRevision, string? Error = null);

/// <summary>Same-directory replacement; a failed read never authorizes overwriting user data.</summary>
public sealed class JsonSettingsStore<T> where T : class
{
    private static readonly ConcurrentDictionary<string, object> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly JsonSerializerOptions _options;
    private readonly Func<T> _defaults;
    private readonly Action<T>? _validate;
    private readonly object _gate;
    private readonly object _stateGate = new();
    private readonly List<Task<SaveResult>> _pending = new();
    private long _revision, _persisted;
    private byte[]? _latest;
    private bool _loaded, _protected;
    private T? _value;
    public SettingsReadState ReadState { get; private set; }
    public string? ReadError { get; private set; }
    public bool IsDirty { get { lock (_stateGate) return _revision > _persisted; } }
    public bool IsProtected => _protected;
    public string FilePath => _path;

    public JsonSettingsStore(string path, Func<T> defaults, JsonSerializerOptions? options = null, Action<T>? validate = null)
    {
        _path = Path.GetFullPath(path);
        _defaults = defaults;
        _options = options ?? new JsonSerializerOptions { WriteIndented = true };
        _validate = validate;
        _gate = FileGates.GetOrAdd(_path, _ => new object());
    }

    public T Read()
    {
        lock (_gate)
        {
            if (_loaded) return _value!;
            try
            {
                _value = ReadFile(_path);
                ReadState = SettingsReadState.Valid;
                _loaded = true;
                return _value;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                ReadError = error.Message;
                var missing = error is FileNotFoundException or DirectoryNotFoundException;
                if (missing && !File.Exists(_path + ".bak"))
                {
                    ReadState = SettingsReadState.NewFile;
                    _value = _defaults(); _loaded = true;
                    return _value;
                }
                // A newer schema must never be replaced with an older backup.
                if (error is not UnsupportedSettingsSchemaException)
                {
                    try
                    {
                        var recovered = ReadFile(_path + ".bak");
                        if (!missing) File.Copy(_path, _path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                        // Do not overwrite the good .bak with the corrupt primary during recovery.
                        Replace(JsonSerializer.SerializeToUtf8Bytes(recovered, _options), preserveBackup: true);
                        ReadState = SettingsReadState.Recovered;
                        Log.Warning("设置已从备份恢复：{Path}", _path);
                        _value = recovered; _loaded = true;
                        return _value;
                    }
                    catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        Log.Error(backupError, "设置与备份不可恢复，进入只读保护：{Path}", _path);
                    }
                }
                _protected = true;
                ReadState = SettingsReadState.ProtectedDefaults;
                _value = _defaults(); _loaded = true;
                return _value;
            }
        }
    }

    private T ReadFile(string file)
    {
        var json = File.ReadAllText(file);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("设置必须为 JSON 对象");
        foreach (var key in new[] { "SchemaVersion", "$schemaVersion" })
            if (document.RootElement.TryGetProperty(key, out var version)
                && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number > 1 || number < 0))
                throw new UnsupportedSettingsSchemaException("设置版本不受支持，已保护原文件");
        var value = JsonSerializer.Deserialize<T>(json, _options) ?? throw new JsonException("设置不能为空");
        _validate?.Invoke(value);
        return value;
    }

    private (long Revision, byte[] Bytes) Snapshot(T value)
    {
        Read();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        lock (_stateGate) { _value = value; _latest = bytes; return (++_revision, bytes); }
    }

    public void Save(T value)
    {
        var snapshot = Snapshot(value);
        var result = Persist(snapshot.Revision, snapshot.Bytes);
        if (!result.Succeeded) throw new SettingsWriteException(_path, result.Error!);
    }

    public Task<SaveResult> SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(value); // Serialize on the caller's owning thread.
        var task = Task.Run(() => cancellationToken.IsCancellationRequested
            ? new SaveResult(false, _persisted, "保存已取消") : Persist(snapshot.Revision, snapshot.Bytes));
        lock (_stateGate) { _pending.RemoveAll(t => t.IsCompleted); _pending.Add(task); }
        return task;
    }

    public async Task<SaveResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        Task<SaveResult>[] pending;
        lock (_stateGate) pending = _pending.ToArray();
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        long revision; byte[]? bytes;
        lock (_stateGate) { revision = _revision; bytes = _latest; }
        return bytes is null ? new SaveResult(true, _persisted)
            : await Task.Run(() => Persist(revision, bytes)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SaveResult Persist(long revision, byte[] bytes)
    {
        lock (_gate)
        {
            if (_protected) return new(false, _persisted, "原设置损坏或版本不兼容，已阻止覆盖：" + _path);
            lock (_stateGate) if (revision <= _persisted) return new(true, _persisted);
            try
            {
                Replace(bytes, preserveBackup: false);
                lock (_stateGate) _persisted = revision;
                return new(true, revision);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Error(error, "设置未保存，保留上一有效文件：{Path}", _path);
                return new(false, _persisted, error.Message);
            }
        }
    }

    /// <summary>Explicit recovery only; archive all existing source files before creating defaults.</summary>
    public T ResetToDefaults()
    {
        lock (_gate)
        {
            if (!_protected) throw new InvalidOperationException("设置未处于保护模式");
            var suffix = ".before-reset-" + Guid.NewGuid().ToString("N");
            if (File.Exists(_path)) File.Copy(_path, _path + suffix);
            if (File.Exists(_path + ".bak")) File.Copy(_path + ".bak", _path + suffix + ".bak");
            var value = _defaults(); _validate?.Invoke(value);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            Replace(bytes, preserveBackup: true);
            lock (_stateGate) { _latest = bytes; _persisted = ++_revision; _value = value; }
            _protected = false; _loaded = true; ReadError = null; ReadState = SettingsReadState.Valid;
            return value;
        }
    }

    private void Replace(byte[] bytes, bool preserveBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(_path)) File.Replace(temp, _path, preserveBackup ? null : _path + ".bak");
            else File.Move(temp, _path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class SettingsWriteException(string path, string message) : IOException($"设置未保存（{path}）：{message}");
public sealed class UnsupportedSettingsSchemaException(string message) : IOException(message);
