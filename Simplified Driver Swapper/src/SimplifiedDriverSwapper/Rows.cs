namespace SimplifiedDriverSwapper;

/// <summary>One line of the grid table. Rebuilt on every refresh rather than bound live.</summary>
public sealed class SeatRow
{
    public required int Slot { get; init; }
    public required string Team { get; init; }
    public required string Driver { get; init; }

    /// <summary>Whether this seat's driver is the game's or one that was chosen.</summary>
    public required string Source { get; init; }

    public required string Number { get; init; }
    public required string Helmet { get; init; }
    public required string Suit { get; init; }
    public required string Gloves { get; init; }

    /// <summary>Drives the row highlight — true when the save would touch this seat.</summary>
    public required bool IsChanged { get; init; }
}

/// <summary>One line of the drivers table, showing pending edits rather than what's on disk.</summary>
public sealed class DriverRow
{
    public required int Index { get; init; }
    public required string DriverId { get; init; }
    public required string Initials { get; init; }
    public required string Nationality { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string FullName { get; init; }
    public required string Head { get; init; }
    public required string AudioName { get; init; }
    public required bool IsChanged { get; init; }
}
