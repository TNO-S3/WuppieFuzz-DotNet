# Pet Store Example API

A minimal ASP.NET Core API demonstrating a toy pet store with in-memory data storage. Designed for testing and fuzzing with WuppieFuzz.

## Features

- **In-memory storage**: Pets are stored in a thread-safe dictionary
- **CRUD operations**: Create, read, update (full and partial), and delete pets
- **OpenAPI/Swagger**: Full OpenAPI 3.0 specification included
- **Sample data**: Pre-populated with two sample pets on startup
- **Thread-safe**: All storage operations are protected with locks

## Endpoints

### Health Check
- `GET /health` — Returns `200 OK`

### Pet Management
- `GET /pet` — Returns list of all pet IDs
- `POST /pet` — Create a new pet
- `GET /pet/{id}` — Get pet details
- `PUT /pet/{id}` — Full update (replaces entire pet)
- `PATCH /pet/{id}` — Partial update (updates individual fields)
- `DELETE /pet/{id}` — Delete a pet

## Building

```bash
cd coverage_agents/dotnet/example_app
dotnet build
```

## Running

```bash
cd coverage_agents/dotnet/example_app
dotnet run
```

The API will start on `http://localhost:5000` by default.

### Accessing Swagger UI

Once running, visit `http://localhost:5000/swagger/ui` to see the interactive API documentation.

## Running with coverage agent

To run this example app with the WuppieFuzz coverage agent:

```bash
# Terminal 1: Start the coverage agent
dotnet run --project coverage_agents/dotnet/

# Terminal 2: Start this example app with coverage
dotnet-coverage connect wuppiefuzz dotnet run --project coverage_agents/dotnet/example_app/

# Terminal 3: Run WuppieFuzz
wuppiefuzz fuzz \
  --coverage-format dotnet \
  --coverage-host 127.0.0.1:6302 \
  coverage_agents/dotnet/example_app/openapi.yaml
```

## API Examples

### Create a Pet
```bash
curl -X POST http://localhost:5000/pet \
  -H "Content-Type: application/json" \
  -d '{"name": "Max", "type": "dog", "age": 2}'
```

### Get all pet IDs
```bash
curl http://localhost:5000/pet
```

### Get pet by ID
```bash
curl http://localhost:5000/pet/1
```

### Update pet (full replacement)
```bash
curl -X PUT http://localhost:5000/pet/1 \
  -H "Content-Type: application/json" \
  -d '{"name": "Max Updated", "type": "dog", "age": 3, "status": "sold"}'
```

### Partially update pet
```bash
curl -X PATCH http://localhost:5000/pet/1 \
  -H "Content-Type: application/json" \
  -d '{"age": 4}'
```

### Delete pet
```bash
curl -X DELETE http://localhost:5000/pet/1
```

## Project Structure

- `Program.cs` — API endpoints and startup configuration
- `Models/` — Data models (Pet, CreatePetRequest, UpdatePetRequest)
- `openapi.yaml` — OpenAPI 3.0 specification
- `example_app.csproj` — Project file
