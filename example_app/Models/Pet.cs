namespace example_app.Models;

/// <summary>
/// Represents a pet in the store.
/// </summary>
public class Pet
{
    /// <summary>
    /// Unique identifier for the pet.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The name of the pet.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The type of animal (e.g., "dog", "cat").
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Current status of the pet (e.g., "available", "adopted", "pending").
    /// </summary>
    public string Status { get; set; } = "available";

    /// <summary>
    /// Age of the pet in years.
    /// </summary>
    public int Age { get; set; }

    /// <summary>
    /// Optional description or notes about the pet.
    /// </summary>
    public string? Description { get; set; }
}
