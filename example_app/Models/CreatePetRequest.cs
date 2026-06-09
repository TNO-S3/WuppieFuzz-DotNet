namespace example_app.Models;

/// <summary>
/// Request body for creating a new pet.
/// </summary>
public class CreatePetRequest
{
    /// <summary>
    /// The name of the pet.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The type of animal (e.g., "dog", "cat").
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Age of the pet in years.
    /// </summary>
    public int Age { get; set; }

    /// <summary>
    /// Optional description or notes about the pet.
    /// </summary>
    public string? Description { get; set; }
}
