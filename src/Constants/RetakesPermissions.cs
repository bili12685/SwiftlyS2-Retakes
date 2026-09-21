namespace SwiftlyS2_Retakes.Constants;

/// <summary>
/// Permission flags for retakes commands.
/// </summary>
public static class RetakesPermissions
{
  public const string Root = "retakes.root";
  public const string Admin = "retakes.admin";

  /// <summary>
  /// Default permission required to receive an AWP when
  /// <c>Allocation.AwpAllowEveryone</c> is false. Server owners grant it through
  /// a group in <c>permissions.json</c>; set <c>Allocation.AwpAccessFlag</c> to
  /// an empty string to remove the gate entirely, or to any other permission
  /// name to use a different group.
  /// </summary>
  public const string Vip = "retakes.vip";
}
