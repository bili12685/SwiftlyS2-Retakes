using SwiftlyS2_Retakes.Models;

namespace SwiftlyS2_Retakes.Configuration;

/// <summary>
/// Configuration for weapon allocation.
/// </summary>
public sealed class AllocationConfig
{
  public bool Enabled { get; set; } = true;
  public string RoundType { get; set; } = "random";
  public int RoundTypePctPistol { get; set; } = 20;
  public int RoundTypePctHalf { get; set; } = 30;
  public int RoundTypePctFull { get; set; } = 50;
  public List<RoundTypeSequenceEntry> RoundTypeSequence { get; set; } = new();

  public bool AwpEnabled { get; set; } = true;
  public int AwpPerTeam { get; set; } = 1;

  /// <summary>
  /// When true, every player is authorised to receive an AWP. When false, only
  /// players holding <see cref="AwpAccessFlag"/> are.
  /// In both cases the player's own <c>!awp</c> preference must still be on —
  /// this switch controls who is *allowed*, not who *wants* one.
  /// </summary>
  public bool AwpAllowEveryone { get; set; } = false;

  /// <summary>
  /// Permission required to be eligible for an AWP when
  /// <see cref="AwpAllowEveryone"/> is false. Expects a permission string as
  /// defined in the server's permissions.json (e.g. "retakes.vip"), not a group
  /// name. An empty string disables the gate and authorises everyone.
  /// </summary>
  public string AwpAccessFlag { get; set; } = "retakes.vip";

  public int AwpLowPlayersThreshold { get; set; } = 4;
  public int AwpLowPlayersChance { get; set; } = 50;
  public int AwpLowPlayersVipChance { get; set; } = 60;

  public bool Ssg08Enabled { get; set; } = true;
  public int Ssg08PerTeam { get; set; } = 0;
  public bool Ssg08AllowEveryone { get; set; } = false;

  public bool Ssg08HalfEnabled { get; set; } = true;
  public int Ssg08HalfPerTeam { get; set; } = 0;
  public bool Ssg08HalfAllowEveryone { get; set; } = false;

  public string AwpPriorityFlag { get; set; } = "";
  public int AwpPriorityPct { get; set; } = 0;

  public bool PistolHelmet { get; set; } = false;

  public bool InstantSwap { get; set; } = true;
}
