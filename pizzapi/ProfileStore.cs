using Newtonsoft.Json;

namespace pizzapi;

public sealed class ProfileStore
{
    private sealed class ProfileFile
    {
        public Guid? ActiveProfileId { get; set; }
        public List<ProcessingProfile> Profiles { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _lock = new();

    public ProfileStore(string? path = null)
    {
        _path = path ?? Path.Combine(pizzalib.Settings.DefaultWorkingDirectory, "profiles.json");
    }

    public (List<ProcessingProfile> Profiles, Guid ActiveProfileId) Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                var initial = CreateDefault();
                Save(initial.Profiles, initial.ActiveProfileId);
                return initial;
            }

            try
            {
                var json = File.ReadAllText(_path);
                var file = JsonConvert.DeserializeObject<ProfileFile>(json) ?? new ProfileFile();
                if (file.Profiles.Count == 0)
                {
                    var initial = CreateDefault();
                    Save(initial.Profiles, initial.ActiveProfileId);
                    return initial;
                }

                var active = file.ActiveProfileId ?? file.Profiles[0].Id;
                if (!file.Profiles.Any(p => p.Id == active))
                {
                    active = file.Profiles[0].Id;
                }

                return (file.Profiles, active);
            }
            catch
            {
                var initial = CreateDefault();
                Save(initial.Profiles, initial.ActiveProfileId);
                return initial;
            }
        }
    }

    public void Save(List<ProcessingProfile> profiles, Guid activeProfileId)
    {
        lock (_lock)
        {
            var file = new ProfileFile
            {
                ActiveProfileId = activeProfileId,
                Profiles = profiles
            };

            var json = JsonConvert.SerializeObject(file, Formatting.Indented);
            File.WriteAllText(_path, json);
        }
    }

    private static (List<ProcessingProfile> Profiles, Guid ActiveProfileId) CreateDefault()
    {
        var profile = new ProcessingProfile
        {
            Name = "Default"
        };

        return (new List<ProcessingProfile> { profile }, profile.Id);
    }
}