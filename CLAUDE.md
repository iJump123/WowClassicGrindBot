# WowClassicGrindBot - Claude Code Guidelines

## Project Overview
Multi-project .NET 10 solution (MasterOfPuppets.sln) with Blazor Server frontend, SignalR communication, and various utility projects.

## Language & Framework
- **Target:** .NET 10 (`net10.0`) with C# 14 (`LangVersion: preview`)
- **SDK:** 10.0.100 (defined in `global.json`)
- Use latest C# 14 features: primary constructors, collection expressions, `field` keyword, extension types, etc.
- Nullable reference types are enabled project-wide

## Build & Test Commands
```bash
dotnet build MasterOfPuppets.sln
dotnet test
dotnet run --project BlazorServer
dotnet run --project Benchmarks -c Release
```

## Constants
  - Replace magic strings/numbers with `public const` fields for cross-file discoverability
  - Place constants in the owning class to enable Find All References and compile-time safety

## Performance Guidelines
Follow .NET performance best practices from:
- https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/
- https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/
- https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-8/

Key patterns to apply:
- Before write any new code explore existing similar implementations, do not duplicate code (functions, variables, constants)
- Use Collections type with Empty semantics rather then allocating new collection with 0 elements
- Prefer `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>` over arrays for buffer operations
- Use `stackalloc` for small, fixed-size allocations
- Prefer `ValueTask<T>` over `Task<T>` when results are often synchronous
- Use `ArrayPool<T>.Shared` and `MemoryPool<T>.Shared` for temporary buffers
- Prefer `FrozenDictionary`/`FrozenSet` for read-heavy lookup tables
- Use `SearchValues<T>` for character/byte searching
- Avoid allocations in hot paths - use structs, pooling, and spans
- Use `[InlineArray]` for fixed-size inline buffers
- Prefer `string.Create` and `ISpanFormattable` over string concatenation
- Use `CompositeFormat` for repeated format string usage
- Apply `[SkipLocalsInit]` to performance-critical methods when safe

## Code Style
Existing `.editorconfig` defines style rules. Key conventions:
- File-scoped namespaces
- Explicit types preferred over `var`
- Expression-bodied properties/indexers/accessors, but not methods/constructors
- Use pattern matching and null propagation
- Prefer braces for control statements

## Project Structure
- `BlazorServer/` - Main web application entry point
- `Core/` - Core business logic
- `Game/` - Game interaction layer
- `Frontend/` - Blazor UI components
- `PPather/` - Pathfinding implementation
- `Benchmarks/` - BenchmarkDotNet performance tests
- `SharedLib/` - Shared utilities
- `WinAPI/` - Windows API interop

## Dependencies
Central package management via `Directory.Packages.props`:
- **Logging:** Serilog with structured logging
- **Serialization:** Newtonsoft.Json, MessagePack, MemoryPack
- **UI:** Blazor Bootstrap, MatBlazor
- **Benchmarking:** BenchmarkDotNet

## Architecture Patterns
- Constructor-based dependency injection via `Microsoft.Extensions.DependencyInjection`
- SignalR for real-time client communication (MessagePack protocol)
- Async/await throughout - avoid blocking calls

## Testing
- Run benchmarks in Release mode: `dotnet run --project Benchmarks -c Release`
- Use BenchmarkDotNet `[Benchmark]` attributes for performance testing

## Git Workflow
- Main branch: `dev`
- Create feature branches from `dev`

## Discord Integration

**Location:** `Core/Discord/`
**Package:** Discord.Net 3.17.2 (via `Directory.Packages.props`)
**Config file:** `Json/Discord/discord_config.json` (gitignored - contains secrets)

### Architecture

Two independent services registered as singletons, eagerly resolved in `BlazorServer/Program.cs`:

1. **`DiscordNotificationService`** - One-way webhook notifications (no bot token needed)
2. **`DiscordBotService`** - Interactive two-way bot commands (requires bot token)

Both load config via `DiscordConfig.Load(dataConfig.Root)` which reads from `Json/Discord/discord_config.json`.

### DiscordConfig (`Core/Discord/DiscordConfig.cs`)
- POCO with webhook URL, bot token, notification toggles
- `Load(rootPath)` creates directory + default file if missing
- `Save()` persists changes (called from UI)
- `ShouldNotify(ChatMessageType)` filters which chat types trigger webhooks

### DiscordNotificationService (`Core/Discord/DiscordNotificationService.cs`)
- Sends webhook POST requests with optional screenshot attachments
- Subscribes to `TextReader.Messages.CollectionChanged` for chat forwarding
- Subscribes to `SessionStat.OnDeath` for death alerts
- Runs a `Timer` to check `SessionStat.StuckSeconds` for stuck alerts
- Sends startup notification on boot (3s delay for initialization)
- Tracks `LastWhisperFrom` for reply support
- Uses `MultipartFormDataContent` with `payload_json` field for Discord webhook API

### DiscordBotService (`Core/Discord/DiscordBotService.cs`)
- Uses Discord.Net `DiscordSocketClient` with gateway intents: Guilds, GuildMessages, MessageContent
- All commands prefixed with `!`, routed in `OnMessageReceived`
- **CRITICAL**: Uses `ExecGameCommand.Run()` via `Task.Run()` for game chat commands
  - `ExecGameCommand.Run()` calls `SetForegroundWindow()` + `SendText()` (WM_CHAR messages)
  - Must run on background thread to avoid blocking Discord gateway
- `!status` and `!help` use `EmbedBuilder` with colored embeds
- `!logout`/`!camp` stops bot first via `ToggleBotStatus()` then sends `/camp`
- **NEVER use `IBotController.Shutdown()`** - it calls `cts.Cancel()` which kills the entire app
- **NEVER use `Thread.Sleep`** on gateway thread - use `await Task.Delay` or `Task.Run`
- `Discord.Color` conflicts with `System.Drawing` - use `global::Discord.Color`

### Bot Commands
| Command | Alias | Args | Description |
|---------|-------|------|-------------|
| `!start` | | | Start the bot (`ToggleBotStatus`) |
| `!stop` | | | Stop the bot (`ToggleBotStatus`) |
| `!status` | | | Embed: status, level, XP, bags, HP/MP, kills, session |
| `!screenshot` | `!ss` | | Capture and send screenshot |
| `!say` | | `<msg>` | Send `/say` in-game |
| `!w` | | `<name> <msg>` | Whisper a player |
| `!r` | | `<msg>` | Reply to last whisperer |
| `!g` | | `<msg>` | Guild chat message |
| `!reload` | | | Trigger `/reload` |
| `!hearth` | | | Use Hearthstone |
| `!logout` | `!camp` | | Stop bot + `/camp` |
| `!help` | | | Show command list embed |

### UI Pages
- **`Frontend/Pages/DiscordConfiguration.razor`** - Config page: webhook URL, bot token, notification toggles, test button
- **`Frontend/Pages/Chat.razor`** - Chat UI: message log, send forms (say/whisper/reply/guild), quick actions
  - Chat.razor uses `ExecGameCommand.Run()` directly (no stop/resume needed)
  - Quick actions (Hearth, Logout) use `ToggleBotStatus()` then `exec.Run()`

### DI Registration (`Core/DependencyInjection.cs`)
```csharp
s.AddSingleton<DiscordNotificationService>();
s.AddSingleton<DiscordBotService>();
```

### Eager Resolution (`BlazorServer/Program.cs` ConfigureApp)
```csharp
app.Services.GetService<DiscordNotificationService>();
app.Services.GetService<DiscordBotService>();
```
Singletons are lazy by default - these lines force construction on startup so webhooks and bot connect immediately.

### Known Pitfalls
- **Shutdown() kills the app**: `IBotController.Shutdown()` cancels the root `CancellationTokenSource`. Use `ToggleBotStatus()` to pause/resume.
- **Gateway thread blocking**: Discord.Net's gateway runs on a single thread. Any blocking call (Thread.Sleep, sync I/O) causes missed heartbeats and disconnects.
- **SetForegroundWindow required**: Game commands only work if WoW window is brought to foreground first. `ExecGameCommand.Run()` handles this; raw `WowProcessInput` methods do not.
- **Config contains secrets**: `discord_config.json` has bot token and webhook URL - must stay gitignored. `Json/Discord/.gitkeep` keeps the folder in the repo.

---

## DataToColor WoW Addon (Lua 5.1)

**Location:** `Addons/DataToColor/`

World of Warcraft uses **Lua 5.1** (all versions including Classic). The addon encodes game state as pixel colors for external reading.

### Lua 5.1 Performance Guidelines

**Goal:** Minimize memory allocations and optimize execution time.

#### Local Variable Caching (Critical)
Cache global lookups at file scope - global access is slow:
```lua
-- DO: Cache at file scope
local floor = math.floor
local band = bit.band
local UnitHealth = UnitHealth
local GetTime = GetTime

-- DON'T: Access globals in hot paths
local function update()
    return math.floor(GetTime())  -- Two global lookups per call
end
```

#### Avoid Table Allocations in Loops
```lua
-- DON'T: Creates new table every call
local function getData()
    return { x = 1, y = 2 }
end

-- DO: Reuse pre-allocated tables
local dataCache = { x = 0, y = 0 }
local function getData()
    dataCache.x = 1
    dataCache.y = 2
    return dataCache
end
```

#### String Operations
```lua
-- DON'T: String concatenation creates garbage
local msg = "Player: " .. name .. " HP: " .. hp

-- DO: Use string.format (single allocation)
local msg = string.format("Player: %s HP: %d", name, hp)

-- BETTER for hot paths: Avoid string creation entirely
```

#### Table Pooling for Temporary Objects
```lua
-- Reuse tables instead of creating/discarding
local pool = {}
local function acquire()
    return table.remove(pool) or {}
end
local function release(t)
    wipe(t)  -- WoW API: clears table without dealloc
    pool[#pool + 1] = t
end
```

#### Numeric Operations
```lua
-- Use locals for repeated calculations
local x = someValue
local x2 = x * x  -- Reuse intermediate results

-- Prefer multiplication over division
local half = x * 0.5  -- Faster than x / 2

-- Use bit operations for powers of 2
local doubled = bit.lshift(x, 1)  -- x * 2
local halved = bit.rshift(x, 1)   -- x / 2 (integer)
```

#### Loop Optimization
```lua
-- Cache length outside loop
local len = #items
for i = 1, len do
    -- use items[i]
end

-- Use numeric for-loops over pairs/ipairs when possible
-- pairs/ipairs create iterator closures

-- Avoid function calls in loop conditions
for i = 1, GetNumItems() do  -- DON'T: calls every iteration
```

#### Closure Avoidance
```lua
-- DON'T: Creates new function every call
local function setup(callback)
    frame:SetScript("OnUpdate", function() callback() end)
end

-- DO: Define functions once at file scope
local function onUpdate()
    -- implementation
end
frame:SetScript("OnUpdate", onUpdate)
```

#### WoW-Specific Optimizations
```lua
-- Use C_Timer.After instead of OnUpdate for delayed actions
-- Throttle OnUpdate handlers (don't run every frame)
local elapsed = 0
local THROTTLE = 0.1
local function onUpdate(self, dt)
    elapsed = elapsed + dt
    if elapsed < THROTTLE then return end
    elapsed = 0
    -- actual work
end

-- Batch API calls when possible
-- Cache UnitGUID results when target doesn't change
```

#### Memory-Critical Patterns
- Never create tables in `OnUpdate` handlers
- Pre-size arrays with known sizes: `local t = {nil, nil, nil, nil}`
- Use `wipe(t)` instead of `t = {}` to reuse table memory
- Avoid varargs (`...`) in hot paths - they allocate
- Use `select(n, ...)` sparingly - prefer indexed access

### Addon File Structure
```
Addons/DataToColor/
├── init.lua              - Addon initialization, AceAddon setup
├── DataToColor.lua       - Main frame update loop (performance critical)
├── Constants.lua         - Static data tables
├── Query.lua             - Game state queries
├── Storage.lua           - Data storage structures
├── EventHandlers.lua     - WoW event handling
├── Collections.lua       - Data structure implementations
├── ActionBarTextures.lua - Action bar texture tracking
├── ActionBarMacros.lua   - Macro detection
└── libs/                 - Ace3 libraries (external, don't modify)
```
