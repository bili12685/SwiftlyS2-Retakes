namespace SwiftlyS2_Retakes.Configuration;

/// <summary>
/// Configuration for weapons.
/// </summary>
public sealed class WeaponsConfig
{
  public bool BuyMenuEnabled { get; set; } = true;

  public DefaultWeaponsConfig Defaults { get; set; } = new();

  /// <summary>
  /// Pistols available to each team, in the same <c>All</c>/<c>T</c>/<c>Ct</c> shape as
  /// <see cref="HalfBuy"/> and <see cref="FullBuy"/>. Used as the secondary pool on
  /// every round type and as the primary pool on pistol rounds.
  /// </summary>
  public RoundWeaponsConfig Pistols { get; set; } = new()
  {
    All = DefaultPistols(),
  };

  /// <summary>
  /// Pistols for one team: the team-specific list when it is non-empty, otherwise the
  /// shared <c>All</c> list, otherwise the built-in defaults (so emptying every bucket
  /// falls back to something usable rather than leaving players with no sidearm).
  /// </summary>
  public List<string> GetPistols(bool isCt)
  {
    var teamList = isCt ? Pistols.Ct : Pistols.T;
    if (teamList.Count > 0) return teamList;
    if (Pistols.All.Count > 0) return Pistols.All;
    return DefaultPistols();
  }

  /// <summary>
  /// Every pistol configured for either team, for callers that have no team context
  /// (e.g. the buy menu's global allowed-weapons set).
  /// </summary>
  public List<string> GetAllPistols()
  {
    var all = new List<string>(Pistols.All);
    all.AddRange(Pistols.T);
    all.AddRange(Pistols.Ct);
    return all.Count > 0 ? all : DefaultPistols();
  }

  private static List<string> DefaultPistols() => new()
  {
    "weapon_glock",
    "weapon_usp_silencer",
    "weapon_hkp2000",
    "weapon_p250",
    "weapon_fiveseven",
    "weapon_tec9",
    "weapon_cz75a",
    "weapon_deagle",
    "weapon_revolver",
    "weapon_elite",
  };

  public RoundWeaponsConfig HalfBuy { get; set; } = new()
  {
    T = new() { "weapon_galilar", "weapon_mac10", "weapon_mp7", "weapon_ump45", "weapon_nova", "weapon_xm1014", "weapon_sawedoff" },
    Ct = new() { "weapon_famas", "weapon_mp9", "weapon_mp7", "weapon_ump45", "weapon_nova", "weapon_xm1014", "weapon_mag7" },
  };

  public RoundWeaponsConfig FullBuy { get; set; } = new()
  {
    T = new() { "weapon_ak47", "weapon_sg556" },
    Ct = new() { "weapon_m4a1", "weapon_m4a1_silencer", "weapon_aug" },
  };
}

/// <summary>
/// Configuration for server-defined default loadouts when players have no saved preference yet.
/// </summary>
public sealed class DefaultWeaponsConfig
{
  public DefaultRoundLoadoutConfig Pistol { get; set; } = new();
  public DefaultRoundLoadoutConfig HalfBuy { get; set; } = new();
  public DefaultRoundLoadoutConfig FullBuy { get; set; } = new();
}

/// <summary>
/// Default loadout for a given round type.
/// </summary>
public sealed class DefaultRoundLoadoutConfig
{
  public DefaultWeaponSelectionConfig Primary { get; set; } = new();
  public DefaultWeaponSelectionConfig Secondary { get; set; } = new();
}

/// <summary>
/// Team-aware default weapon selection.
/// </summary>
public sealed class DefaultWeaponSelectionConfig
{
  public string? T { get; set; }
  public string? Ct { get; set; }
}

/// <summary>
/// Configuration for weapons per round type.
/// </summary>
public sealed class RoundWeaponsConfig
{
  public List<string> All { get; set; } = new();
  public List<string> T { get; set; } = new();
  public List<string> Ct { get; set; } = new();
}
