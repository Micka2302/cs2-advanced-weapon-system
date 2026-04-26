namespace AdvancedWeaponSystem;

public class Config
{
    public bool AutoUpdateSignatures { get; set; } = true;

    public string GameDataUpdateUrl { get; set; } = "https://raw.githubusercontent.com/Micka2302/cs2-advanced-weapon-system/main/gamedata.json";

    public bool EnableCanAcquireHook { get; set; } = true;

    public Dictionary<string, WeaponData> WeaponDatas { get; set; } = [];

    public class WeaponData
    {
        public string Weapon { get; set; } = string.Empty;
        public int? Clip { get; set; }
        public int? Magazines { get; set; }
        public int? Ammo { get; set; }
        public bool? BlockUsing { get; set; }
        public bool? IgnorePickUpFromBlockUsing { get; set; }
        public bool? ReloadAfterShoot { get; set; }
        public bool? UnlimitedMagazines { get; set; }
        public bool? UnlimitedAmmo { get; set; }
        public bool? UnlimitedClip { get; set; }
        public bool? OnlyHeadshot { get; set; }
        public List<string> AdminFlagsToIgnoreBlockUsing { get; set; } = [];
        public Dictionary<int, int> WeaponQuota { get; set; } = [];
        public Dictionary<string, Dictionary<int, int>> MapSpecificQuota { get; set; } = [];
        public string? Damage { get; set; }
    }
}
