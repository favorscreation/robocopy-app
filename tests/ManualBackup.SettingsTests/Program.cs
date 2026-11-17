using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ManualBackup.App;

var cases = new (string Name, Action Run)[]
{
    ("Legacy restore paths migrate to one paired row exactly once", RestoreMigration),
    ("Restore pairs persist independently and explicit empty lists stay empty", RestoreRoundTrip),
    ("Null restore rows are rejected and null path strings are normalized", RestoreInvalidRows),
    ("Legacy single source migrates with direct destination mapping", LegacyMigration),
    ("Null source collection migrates legacy path", NullCollectionMigration),
    ("Explicit empty source collection suppresses stale legacy path", ExplicitEmpty),
    ("Multiple sources round-trip in order and restore fields stay independent", MultiRoundTrip),
    ("Removing all migrated sources and saving never resurrects legacy path", RemoveAll),
    ("Existing multi-source list wins over stale legacy path", ExistingSources),
    ("Null strings and missing collections receive safe defaults", NullFields),
    ("Invalid JSON is warned and preserved", CorruptJson),
    ("Null source entry is warned and preserved", NullEntry),
    ("Source entries and collection replacement notify changes", Notifications),
    ("Per-profile parallel limits round-trip independently", ParallelLimitsRoundTrip),
    ("Invalid and null parallel limits keep profiles and default to two", InvalidParallelLimits),
    ("Invalid in-memory parallel limits are saved as the default", InvalidParallelLimitSave),
    ("Parallel limit changes notify and do not repeat unchanged values", ParallelLimitNotifications),
    ("Missing settings return empty defaults without writing", MissingSettings)
};
var failed = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}\n{ex}"); }
}
Console.WriteLine($"{cases.Length - failed} passed, {failed} failed ({cases.Length} settings tests). Temporary fixtures cleaned.");
return failed == 0 ? 0 : 1;

static void RestoreMigration()
{
    using var fixture = new Fixture();
    foreach (var optional in new[] { "", ",\"RestoreFolders\":null" })
    {
        fixture.Write("{\"Profiles\":[{\"RestoreSourcePath\":\"E:\\\\Backup\",\"RestoreDestinationPath\":\"C:\\\\Work\"" + optional + "}]}");
        var settings = fixture.Store.Load(out var warning);
        Equal<string?>(null, warning, "restore migration warning");
        var profile = settings.Profiles.Single();
        Equal(1, profile.RestoreFolders.Count, "one migrated pair");
        Equal(profile.RestoreSourcePath, profile.RestoreFolders[0].SourcePath, "migrated source");
        Equal(profile.RestoreDestinationPath, profile.RestoreFolders[0].DestinationPath, "migrated destination");
        fixture.Store.Save([profile], profile.Id);
        Equal(1, fixture.Store.Load(out _).Profiles.Single().RestoreFolders.Count, "no duplicate migration");
    }
}

static void RestoreRoundTrip()
{
    using var fixture = new Fixture();
    var profile = new BackupProfile
    {
        DestinationPath = @"E:\Backup", RestoreSourcePath = @"E:\Stale", RestoreDestinationPath = @"C:\Stale",
        RestoreFolders = [new() { SourcePath = @"E:\Backup\A", DestinationPath = @"C:\A" }, new() { SourcePath = @"F:\B", DestinationPath = @"D:\B" }]
    };
    fixture.Store.Save([profile], profile.Id);
    var loaded = fixture.Store.Load(out var warning).Profiles.Single();
    Equal<string?>(null, warning, "paired roundtrip warning");
    True(profile.RestoreFolders.Select(entry => (entry.SourcePath, entry.DestinationPath)).SequenceEqual(loaded.RestoreFolders.Select(entry => (entry.SourcePath, entry.DestinationPath))), "ordered pairs preserved");
    Equal(profile.DestinationPath, loaded.DestinationPath, "backup path independent");
    loaded.RestoreFolders.Clear();
    fixture.Store.Save([loaded], loaded.Id);
    Equal(0, fixture.Store.Load(out _).Profiles.Single().RestoreFolders.Count, "deleted restore rows never resurrect");
    var notifications = new List<string?>();
    profile.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
    profile.RestoreFolders = [];
    True(notifications.Contains(nameof(BackupProfile.RestoreFolders)), "replacement notifies");
    var row = new RestoreFolderEntry();
    row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
    row.SourcePath = "source"; row.DestinationPath = "target";
    True(notifications.Contains(nameof(RestoreFolderEntry.SourcePath)) && notifications.Contains(nameof(RestoreFolderEntry.DestinationPath)), "row edits notify");
}

static void RestoreInvalidRows()
{
    using var fixture = new Fixture();
    fixture.Write("""{"Profiles":[{"RestoreFolders":[{"SourcePath":null,"DestinationPath":null}]}]}""");
    var profile = fixture.Store.Load(out var warning).Profiles.Single();
    Equal<string?>(null, warning, "null paths normalized");
    Equal("", profile.RestoreFolders.Single().SourcePath, "null source");
    Equal("", profile.RestoreFolders.Single().DestinationPath, "null destination");
    fixture.Write("""{"Profiles":[{"RestoreFolders":[null]}]}""");
    Equal(0, fixture.Store.Load(out warning).Profiles.Count, "invalid list rejected");
    True(warning is not null, "invalid row warned");
}

static void LegacyMigration()
{
    using var fixture = new Fixture();
    fixture.Write("""
        {"SelectedId":"legacy","Profiles":[{"Id":"legacy","Name":"以前の設定","SourcePath":"C:\\Data\\業務","DestinationPath":"E:\\Backup","LastRun":"done","RestoreSourcePath":"F:\\Old","RestoreDestinationPath":"C:\\Recovered","RestoreLastRun":"restored"}]}
        """);
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "legacy warning");
    Equal("legacy", settings.SelectedId, "selected ID");
    var profile = settings.Profiles.Single();
    Equal(1, profile.SourceFolders.Count, "migrated source count");
    Equal(@"C:\Data\業務", profile.SourceFolders[0].SourcePath, "migrated path");
    Equal("", profile.SourceFolders[0].TargetFolderName, "legacy maps directly to destination, without new child folder");
    Equal(@"E:\Backup", profile.DestinationPath, "destination unchanged");
    Equal("", profile.SourcePath, "legacy field cleared immediately");
    Equal(@"F:\Old", profile.RestoreSourcePath, "restore source unchanged");
    Equal(@"C:\Recovered", profile.RestoreDestinationPath, "restore destination unchanged");
    Equal("restored", profile.RestoreLastRun, "restore status unchanged");
    Equal(2, profile.MaxParallelSources, "old profile defaults to two parallel sources");
    fixture.Store.Save(settings.Profiles, settings.SelectedId);
    using var json = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
    Equal("", json.RootElement.GetProperty("Profiles")[0].GetProperty("SourcePath").GetString(), "saved legacy field");
    var again = fixture.Store.Load(out warning);
    Equal<string?>(null, warning, "roundtrip warning");
    Equal(1, again.Profiles.Single().SourceFolders.Count, "migration happens once");
    Equal(2, again.Profiles.Single().MaxParallelSources, "default parallel limit survives migrated save");
}

static void NullCollectionMigration()
{
    using var fixture = new Fixture();
    fixture.Write("""{"Profiles":[{"SourcePath":"C:\\Legacy","SourceFolders":null}]}""");
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "null collection warning");
    Equal(@"C:\Legacy", settings.Profiles.Single().SourceFolders.Single().SourcePath, "null collection migration");
    Equal("", settings.Profiles.Single().SourcePath, "legacy cleared");
}

static void ExplicitEmpty()
{
    using var fixture = new Fixture();
    fixture.Write("""{"Profiles":[{"SourcePath":"C:\\Stale","SourceFolders":[]}]}""");
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "empty collection warning");
    Equal(0, settings.Profiles.Single().SourceFolders.Count, "intentional empty retained");
    Equal("", settings.Profiles.Single().SourcePath, "stale legacy cleared");
    fixture.Store.Save(settings.Profiles, null);
    Equal(0, fixture.Store.Load(out _).Profiles.Single().SourceFolders.Count, "empty persists");
}

static void MultiRoundTrip()
{
    using var fixture = new Fixture();
    var profile = new BackupProfile
    {
        Id = "multi", Name = "複数フォルダー", DestinationPath = @"E:\Backup", LastRun = "backup status",
        RestoreSourcePath = @"F:\RestoreSource", RestoreDestinationPath = @"D:\RestoreTarget", RestoreLastRun = "restore status",
        SourceFolders = [new BackupSourceEntry { SourcePath = @"C:\First", TargetFolderName = "書類" },
            new BackupSourceEntry { SourcePath = @"D:\Second", TargetFolderName = "写真 2026" },
            new BackupSourceEntry { SourcePath = @"C:\LegacyDirect", TargetFolderName = "" }]
    };
    fixture.Store.Save([profile], "multi");
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "multi roundtrip warning");
    Equal("multi", settings.SelectedId, "selected profile");
    var loaded = settings.Profiles.Single();
    Equal(profile.Name, loaded.Name, "name");
    Equal(profile.DestinationPath, loaded.DestinationPath, "destination");
    Equal(profile.LastRun, loaded.LastRun, "last backup");
    Equal(profile.RestoreSourcePath, loaded.RestoreSourcePath, "restore source");
    Equal(profile.RestoreDestinationPath, loaded.RestoreDestinationPath, "restore target");
    Equal(profile.RestoreLastRun, loaded.RestoreLastRun, "restore status");
    Equal(3, loaded.SourceFolders.Count, "source count");
    for (var i = 0; i < profile.SourceFolders.Count; i++)
    {
        Equal(profile.SourceFolders[i].SourcePath, loaded.SourceFolders[i].SourcePath, "source order/path " + i);
        Equal(profile.SourceFolders[i].TargetFolderName, loaded.SourceFolders[i].TargetFolderName, "alias order " + i);
    }
    True(!Directory.GetFiles(fixture.Root, "*.tmp").Any(), "atomic save left no temp files");
}

static void RemoveAll()
{
    using var fixture = new Fixture();
    fixture.Write("""{"Profiles":[{"SourcePath":"C:\\Legacy"}]}""");
    var profile = fixture.Store.Load(out _).Profiles.Single();
    Equal(1, profile.SourceFolders.Count, "precondition migrated");
    profile.SourceFolders.Clear();
    // A stale in-memory legacy value must also be erased by Save.
    profile.SourcePath = @"C:\StaleAgain";
    fixture.Store.Save([profile], profile.Id);
    Equal("", profile.SourcePath, "save clears in-memory legacy");
    var loaded = fixture.Store.Load(out var warning).Profiles.Single();
    Equal<string?>(null, warning, "removed all warning");
    Equal(0, loaded.SourceFolders.Count, "removed source must not reappear");
    Equal("", loaded.SourcePath, "saved legacy remains empty");
}

static void ExistingSources()
{
    using var fixture = new Fixture();
    fixture.Write("""{"Profiles":[{"SourcePath":"C:\\Stale","SourceFolders":[{"SourcePath":"D:\\Actual","TargetFolderName":"actual"}]}]}""");
    var profile = fixture.Store.Load(out var warning).Profiles.Single();
    Equal<string?>(null, warning, "existing collection warning");
    Equal(1, profile.SourceFolders.Count, "stale path must not append");
    Equal(@"D:\Actual", profile.SourceFolders.Single().SourcePath, "existing source retained");
    Equal("actual", profile.SourceFolders.Single().TargetFolderName, "existing alias retained");
    Equal("", profile.SourcePath, "stale field cleared");
}

static void NullFields()
{
    using var fixture = new Fixture();
    fixture.Write("""{"Profiles":[{"Id":null,"Name":null,"SourcePath":null,"DestinationPath":null,"LastRun":null,"RestoreSourcePath":null,"RestoreDestinationPath":null,"RestoreLastRun":null,"SourceFolders":[{"SourcePath":null,"TargetFolderName":null}]},{"SourceFolders":null}]}""");
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "null string warning");
    var profile = settings.Profiles[0];
    True(!string.IsNullOrWhiteSpace(profile.Id), "generated profile ID");
    True(!string.IsNullOrWhiteSpace(profile.Name), "default name");
    Equal("", profile.SourcePath, "default legacy source");
    Equal("", profile.DestinationPath, "default destination");
    Equal("", profile.RestoreSourcePath, "default restore source");
    Equal("", profile.RestoreDestinationPath, "default restore destination");
    Equal("", profile.SourceFolders.Single().SourcePath, "default entry path");
    Equal("", profile.SourceFolders.Single().TargetFolderName, "default entry alias");
    Equal(0, settings.Profiles[1].SourceFolders.Count, "default null collection");
}

static void CorruptJson()
{
    using var fixture = new Fixture();
    const string bad = "{ broken settings";
    fixture.Write(bad);
    var settings = fixture.Store.Load(out var warning);
    True(!string.IsNullOrWhiteSpace(warning), "corrupt settings must warn");
    Equal(0, settings.Profiles.Count, "corrupt fallback empty");
    var backup = Directory.GetFiles(fixture.Root, "settings.json.unreadable-*").Single();
    Equal(bad, File.ReadAllText(backup), "corrupt settings preserved");
    Equal(bad, File.ReadAllText(fixture.SettingsPath), "Load must not replace corrupt original");
}

static void NullEntry()
{
    using var fixture = new Fixture();
    const string bad = """{"Profiles":[{"SourceFolders":[null]}]}""";
    fixture.Write(bad);
    var settings = fixture.Store.Load(out var warning);
    True(!string.IsNullOrWhiteSpace(warning), "null source entry must warn");
    Equal(0, settings.Profiles.Count, "null entry fallback");
    Equal(bad, File.ReadAllText(Directory.GetFiles(fixture.Root, "settings.json.unreadable-*").Single()), "invalid source entry preserved");
}

static void Notifications()
{
    var entry = new BackupSourceEntry();
    var notifications = new List<string?>();
    entry.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
    entry.SourcePath = @"C:\A";
    entry.TargetFolderName = "A";
    True(notifications.SequenceEqual([nameof(BackupSourceEntry.SourcePath), nameof(BackupSourceEntry.TargetFolderName)]), "entry property changes");
    Equal("", new BackupSourceEntry().TargetFolderName, "default alias empty");
    var profile = new BackupProfile();
    string? changed = null;
    profile.PropertyChanged += (_, e) => changed = e.PropertyName;
    profile.SourceFolders = new ObservableCollection<BackupSourceEntry>();
    Equal(nameof(BackupProfile.SourceFolders), changed, "collection replacement change");
}

static void MissingSettings()
{
    using var fixture = new Fixture(createDirectory: false);
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "missing settings warning");
    Equal(0, settings.Profiles.Count, "missing settings profiles");
    True(!Directory.Exists(fixture.Root), "loading missing settings should not write");
}

static void ParallelLimitsRoundTrip()
{
    using var fixture = new Fixture();
    int[] limits = [1, 2, 3, 4, 5, 6, 7, 8];
    var profiles = limits.Select(limit => new BackupProfile
    {
        Id = "parallel-" + limit,
        MaxParallelSources = limit,
        DestinationPath = @"E:\Backup" + limit,
        RestoreDestinationPath = @"D:\Restore" + limit,
        SourceFolders = [new BackupSourceEntry { SourcePath = @"C:\Source" + limit, TargetFolderName = "source-" + limit }]
    }).ToArray();
    fixture.Store.Save(profiles, "parallel-4");
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "parallel limits warning");
    Equal("parallel-4", settings.SelectedId, "parallel selected profile");
    Equal(limits.Length, settings.Profiles.Count, "parallel profile count");
    for (var i = 0; i < limits.Length; i++)
    {
        var loaded = settings.Profiles[i];
        Equal(limits[i], loaded.MaxParallelSources, "per-profile parallel limit");
        Equal(profiles[i].DestinationPath, loaded.DestinationPath, "per-profile backup destination");
        Equal(profiles[i].RestoreDestinationPath, loaded.RestoreDestinationPath, "per-profile restore destination");
        Equal(profiles[i].SourceFolders.Single().SourcePath, loaded.SourceFolders.Single().SourcePath, "per-profile source");
    }
}

static void InvalidParallelLimits()
{
    using var fixture = new Fixture();
    fixture.Write("""
        {"Profiles":[
          {"Id":"zero","MaxParallelSources":0,"SourcePath":"C:\\LegacyZero","DestinationPath":"E:\\BackupZero","RestoreSourcePath":"F:\\RestoreZero"},
          {"Id":"nine","MaxParallelSources":9,"SourceFolders":[{"SourcePath":"D:\\Nine","TargetFolderName":"nine"}],"DestinationPath":"E:\\BackupNine","RestoreDestinationPath":"D:\\RestoreNine"},
          {"Id":"null","MaxParallelSources":null,"SourceFolders":[{"SourcePath":"D:\\Null","TargetFolderName":"null-source"}],"RestoreLastRun":"still-restored"}
        ]}
        """);
    var settings = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "invalid optional limit must not discard profiles");
    Equal(3, settings.Profiles.Count, "invalid optional limits keep all profiles");
    True(settings.Profiles.All(p => p.MaxParallelSources == 2), "invalid limits use the default");
    Equal(@"C:\LegacyZero", settings.Profiles[0].SourceFolders.Single().SourcePath, "invalid limit does not stop legacy migration");
    Equal("", settings.Profiles[0].SourceFolders.Single().TargetFolderName, "legacy destination mapping unchanged");
    Equal(@"E:\BackupZero", settings.Profiles[0].DestinationPath, "zero-limit destination preserved");
    Equal(@"F:\RestoreZero", settings.Profiles[0].RestoreSourcePath, "zero-limit restore preserved");
    Equal(@"D:\Nine", settings.Profiles[1].SourceFolders.Single().SourcePath, "nine-limit source preserved");
    Equal(@"D:\RestoreNine", settings.Profiles[1].RestoreDestinationPath, "nine-limit restore preserved");
    Equal(@"D:\Null", settings.Profiles[2].SourceFolders.Single().SourcePath, "null-limit source preserved");
    Equal("still-restored", settings.Profiles[2].RestoreLastRun, "null-limit restore status preserved");
}

static void InvalidParallelLimitSave()
{
    using var fixture = new Fixture();
    var profiles = new[] { new BackupProfile { Id = "low", MaxParallelSources = -1 }, new BackupProfile { Id = "high", MaxParallelSources = 100 } };
    fixture.Store.Save(profiles, "low");
    using var json = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
    foreach (var profile in json.RootElement.GetProperty("Profiles").EnumerateArray())
        Equal(2, profile.GetProperty("MaxParallelSources").GetInt32(), "save writes a valid default limit");
    var loaded = fixture.Store.Load(out var warning);
    Equal<string?>(null, warning, "saved default warning");
    True(loaded.Profiles.All(p => p.MaxParallelSources == 2), "saved defaults roundtrip");
}

static void ParallelLimitNotifications()
{
    var profile = new BackupProfile();
    Equal(2, profile.MaxParallelSources, "new profile parallel default");
    var notifications = new List<string?>();
    profile.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
    profile.MaxParallelSources = 4;
    profile.MaxParallelSources = 4;
    profile.MaxParallelSources = 9;
    Equal(2, profile.MaxParallelSources, "invalid assignment uses default");
    True(notifications.SequenceEqual([nameof(BackupProfile.MaxParallelSources), nameof(BackupProfile.MaxParallelSources)]), "parallel limit changes notify only on effective changes");
}

static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
}

sealed class Fixture : IDisposable
{
    private readonly string? _previousDirectory;
    private readonly string _qaRoot;
    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public SettingsStore Store { get; }
    public Fixture(bool createDirectory = true)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        if (!File.Exists(Path.Combine(workspace, "ManualBackup.sln"))) throw new InvalidOperationException("Workspace root not found.");
        _qaRoot = Path.GetFullPath(Path.Combine(workspace, ".qa"));
        if (Directory.Exists(_qaRoot) && (File.GetAttributes(_qaRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Scratch parent must not be a reparse point.");
        Root = Path.Combine(_qaRoot, "settings-test-" + Guid.NewGuid().ToString("N"));
        _previousDirectory = Environment.GetEnvironmentVariable("BACKUPDESK_DATA_DIR");
        Environment.SetEnvironmentVariable("BACKUPDESK_DATA_DIR", Root);
        Store = new SettingsStore();
        if (createDirectory) Directory.CreateDirectory(Root);
    }
    public void Write(string json) => File.WriteAllText(SettingsPath, json);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BACKUPDESK_DATA_DIR", _previousDirectory);
        var target = Path.GetFullPath(Root);
        var qa = Path.GetFullPath(_qaRoot);
        if (!target.StartsWith(qa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("settings-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Cleanup target outside dedicated settings fixture.");
        if (Directory.Exists(target))
        {
            if ((File.GetAttributes(qa) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(target).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Cleanup refuses reparse points.");
            Directory.Delete(target, recursive: true);
        }
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Fixture cleanup incomplete.");
    }
}
