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
  /// <remarks>
  /// Deliberately empty rather than seeded with <see cref="DefaultPistols"/>. The
  /// configuration binder <em>appends</em> to a collection property that already holds
  /// entries instead of replacing them, so a non-empty default here would be merged into
  /// whatever the file specifies. The owner could then add pistols but never remove one:
  /// deleting an entry from <c>All</c> would leave the built-in copy in place, and the
  /// duplicate would also make that weapon appear twice in the menu. The built-in defaults
  /// are applied by <see cref="GetPistols"/> and <see cref="GetAllPistols"/> instead, only
  /// when the configured buckets are empty.
  /// </remarks>
  public RoundWeaponsConfig Pistols { get; set; } = new();

  /// <summary>
  /// Pistols for one team: the team-specific list when it is non-empty, otherwise the
  /// shared <c>All</c> list, otherwise the built-in defaults (so emptying every bucket
  /// falls back to something usable rather than leaving players with no sidearm).
  /// </summary>
  public List<string> GetPistols(bool isCt)
  {
    var combined = Pistols.ForTeam(isCt);
    return combined.Count > 0 ? combined : DefaultPistols();
  }

  /// <summary>
  /// Every pistol configured for either team, for callers that have no team context
  /// (e.g. the buy menu's global allowed-weapons set).
  /// </summary>
  public List<string> GetAllPistols()
  {
    var combined = Pistols.ForAllTeams();
    return combined.Count > 0 ? combined : DefaultPistols();
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
  /// <summary>
  /// Weapons available to both teams. These are added to each team's own list rather
  /// than only standing in for it, so listing a weapon here makes it available to
  /// everyone without having to repeat it under <see cref="T"/> and <see cref="Ct"/>.
  /// </summary>
  public List<string> All { get; set; } = new();

  public List<string> T { get; set; } = new();
  public List<string> Ct { get; set; } = new();

  /// <summary>
  /// Effective list for one team: every entry from <see cref="All"/> followed by that
  /// team's own.
  /// </summary>
  /// <remarks>
  /// Duplicates are dropped case-insensitively, and the <see cref="All"/> entry is the one
  /// kept -- it is listed first, so it wins both the value and its position. A weapon in
  /// both buckets would otherwise appear twice in the menu and, worse, carry double weight
  /// whenever something is picked at random from the list.
  /// </remarks>
  public List<string> ForTeam(bool isCt) => Merge(All, isCt ? Ct : T);

  /// <summary>
  /// Effective list covering both teams, for callers that have no team context (the buy
  /// menu's server-wide allowed set).
  /// </summary>
  public List<string> ForAllTeams() => Merge(Merge(All, T), Ct);

  /// <summary>
  /// Entries in a team list that also appear in <see cref="All"/>. These are exactly what
  /// the resolution above discards (the <see cref="All"/> entry wins), so a caller can
  /// warn that half of what someone configured is having no effect.
  /// </summary>
  public List<string> DuplicatesAgainstAll(List<string> teamList)
  {
    var shared = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var duplicates = new List<string>();

    foreach (var weapon in teamList)
    {
      if (shared.Contains(weapon) && seen.Add(weapon)) duplicates.Add(weapon);
    }

    return duplicates;
  }

  private static List<string> Merge(List<string> first, List<string> second)
  {
    var merged = new List<string>(first.Count + second.Count);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var weapon in first)
    {
      if (seen.Add(weapon)) merged.Add(weapon);
    }

    foreach (var weapon in second)
    {
      if (seen.Add(weapon)) merged.Add(weapon);
    }

    return merged;
  }
}
