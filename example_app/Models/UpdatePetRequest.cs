namespace example_app.Models;

/// <summary>
/// Request body for updating an existing pet (partial or full).
/// </summary>
public class UpdatePetRequest
{
    /// <summary>
    /// The updated name of the pet (optional).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The updated type of animal (optional).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The updated status (optional, e.g., "available", "adopted", "pending").
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// The updated age in years (optional).
    /// </summary>
    public int? Age { get; set; }

    /// <summary>
    /// The updated description (optional).
    /// </summary>
    public string? Description { get; set; }
}
