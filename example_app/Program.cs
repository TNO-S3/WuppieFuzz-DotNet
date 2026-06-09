using example_app.Models;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Pet Store API",
        Version = "v1",
        Description = "A simple pet store API for testing and fuzzing"
    });
});

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pet Store API v1");
    });
}

// In-memory pet storage
var pets = new Dictionary<long, Pet>();
var petIdCounter = 1L;
var petsLock = new object();

// Initialize with sample data
lock (petsLock)
{
    pets[1] = new Pet { Id = 1, Name = "Fluffy", Type = "cat", Age = 3, Status = "available" };
    pets[2] = new Pet { Id = 2, Name = "Buddy", Type = "dog", Age = 5, Status = "available" };
    petIdCounter = 3;
}

// ============ API ENDPOINTS ============

/// <summary>
/// Health check endpoint.
/// </summary>
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .Produces(200)
    .WithSummary("Health check");

/// <summary>
/// Get list of all pet IDs.
/// </summary>
app.MapGet("/pet", () =>
{
    lock (petsLock)
    {
        return Results.Ok(new { petIds = pets.Keys.ToList() });
    }
})
    .WithName("GetPetIds")
    .Produces(200)
    .WithSummary("Get list of all pet IDs");

/// <summary>
/// Create a new pet.
/// </summary>
app.MapPost("/pet", (CreatePetRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Type))
    {
        return Results.BadRequest(new { error = "Name and Type are required" });
    }

    lock (petsLock)
    {
        var newPet = new Pet
        {
            Id = petIdCounter++,
            Name = request.Name,
            Type = request.Type,
            Age = request.Age,
            Description = request.Description,
            Status = "available"
        };
        pets[newPet.Id] = newPet;
        return Results.Created($"/pet/{newPet.Id}", newPet);
    }
})
    .WithName("CreatePet")
    .Produces(201)
    .Produces(400)
    .WithSummary("Create a new pet");

/// <summary>
/// Get pet details by ID.
/// </summary>
app.MapGet("/pet/{id}", (long id) =>
{
    lock (petsLock)
    {
        if (pets.TryGetValue(id, out var pet))
        {
            return Results.Ok(pet);
        }
        return Results.NotFound(new { error = $"Pet with ID {id} not found" });
    }
})
    .WithName("GetPetById")
    .Produces(200)
    .Produces(404)
    .WithSummary("Get pet details by ID");

/// <summary>
/// Update a pet (full replacement).
/// </summary>
app.MapPut("/pet/{id}", (long id, UpdatePetRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Type))
    {
        return Results.BadRequest(new { error = "Name and Type are required for full replacement" });
    }

    lock (petsLock)
    {
        if (!pets.TryGetValue(id, out var pet))
        {
            return Results.NotFound(new { error = $"Pet with ID {id} not found" });
        }

        pet.Name = request.Name;
        pet.Type = request.Type;
        pet.Age = request.Age ?? pet.Age;
        pet.Status = request.Status ?? pet.Status;
        pet.Description = request.Description;

        return Results.Ok(pet);
    }
})
    .WithName("UpdatePetFull")
    .Produces(200)
    .Produces(400)
    .Produces(404)
    .WithSummary("Update pet (full replacement)");

/// <summary>
/// Partially update a pet.
/// </summary>
app.MapPatch("/pet/{id}", (long id, UpdatePetRequest request) =>
{
    lock (petsLock)
    {
        if (!pets.TryGetValue(id, out var pet))
        {
            return Results.NotFound(new { error = $"Pet with ID {id} not found" });
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
            pet.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Type))
            pet.Type = request.Type;
        if (request.Age.HasValue)
            pet.Age = request.Age.Value;
        if (!string.IsNullOrWhiteSpace(request.Status))
            pet.Status = request.Status;
        if (request.Description != null)
            pet.Description = request.Description;

        return Results.Ok(pet);
    }
})
    .WithName("UpdatePetPartial")
    .Produces(200)
    .Produces(404)
    .WithSummary("Partially update pet");

/// <summary>
/// Delete a pet by ID.
/// </summary>
app.MapDelete("/pet/{id}", (long id) =>
{
    lock (petsLock)
    {
        if (pets.Remove(id))
        {
            return Results.NoContent();
        }
        return Results.NotFound(new { error = $"Pet with ID {id} not found" });
    }
})
    .WithName("DeletePet")
    .Produces(204)
    .Produces(404)
    .WithSummary("Delete a pet by ID");

app.Run();
