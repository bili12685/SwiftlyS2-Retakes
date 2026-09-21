using SwiftlyS2_Retakes.Models;

namespace SwiftlyS2_Retakes.Interfaces;

/// <summary>
/// Service for weapon allocation.
/// </summary>
public interface IAllocationService
{
  /// <summary>
  /// Gets the current round type.
  /// </summary>
  RoundType? CurrentRoundType { get; }

  /// <summary>
  /// Whether the player is authorised to receive an AWP at all, independent of
  /// their own <c>!awp</c> preference. True when
  /// <c>Allocation.AwpAllowEveryone</c> is on, or when
  /// <c>Allocation.AwpAccessFlag</c> is empty, or when the player holds that
  /// permission.
  /// </summary>
  bool IsAuthorisedForAwp(ulong steamId);

  /// <summary>
  /// Whether instant weapon swap on preference change is enabled.
  /// </summary>
  bool InstantSwapEnabled { get; }

  /// <summary>
  /// Selects the round type for the current round.
  /// </summary>
  /// <returns>The selected round type</returns>
  RoundType SelectRoundType();

  /// <summary>
  /// Pre-selects the round type and stores it in <see cref="CurrentRoundType"/>.
  /// Call this before the round starts so convars can be applied in time.
  /// </summary>
  void PreSelectRoundType();

  /// <summary>
  /// Allocates weapons for all current players.
  /// </summary>
  /// <param name="pawnLifecycle">The pawn lifecycle service</param>
  void AllocateForCurrentPlayers(IPawnLifecycleService pawnLifecycle);
}
