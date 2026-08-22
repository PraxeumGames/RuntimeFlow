# Content, platform and remote sources

Universal pattern for the concerns every game has: **remote config**, **content/catalog
initialization (Addressables etc.)**, and **game-service authentication (Google Play
Games, Game Center, Steam)** — plus the degraded-mode policy for each.

## The primitive

`RuntimeFlow.Content.ContentSource<TData>` — one small subclass per concern:

```csharp
public sealed class RemoteConfigSource : ContentSource<RemoteConfigSnapshot>
{
    public override string SourceName => "remote-config";

    protected override async Task<RemoteConfigSnapshot> LoadAsync(CancellationToken ct)
    {
        var json = await _backend.FetchDefaults(ct);
        return JsonSerializer.Deserialize<RemoteConfigSnapshot>(json)!;
    }
}
```

Registration is one line in the scope where the data belongs:

```csharp
builder.Global().Content<RemoteConfigSource, RemoteConfigSnapshot>();
```

Consumers declare `[DependsOn(typeof(RemoteConfigSource))]` (same scope) or rely on the
scope hierarchy (global content is ready before any session consumer) and inject
`ContentSource<TData>` / `IContentSource<TData>`.

Everything else is inherited from the pipeline: wave scheduling with `[DependsOn]` edges,
health timeouts, retries, progress reporting, cancellation, dashboard visibility.

## Stage placement

| Concern | Scope | Stage markers to combine |
|---|---|---|
| Remote config | Global | none (`IAsyncInitializableService`) or session `IPreBootstrapStartupInitializableService` when user-scoped |
| Addressables/catalog | Session | `IContentStartupInitializableService` |
| Platform sign-in | Session | `IPlatformStartupInitializableService` |
| Sign-in with consent dialog | Session | + `IUserInteractionGatedInitializableService` (health watchdog exempted while the dialog is open) |

## Degraded mode (optional sources)

Set `IsOptional = true` and provide `FallbackData`: a failed fetch logs, invokes
`OnLoadFailedAsync` (metrics/breadcrumbs), serves the fallback snapshot and marks
`UsedFallback = true`. The pipeline stays green; gameplay reads defaults. Required sources
(default) fail startup — explicit choice per source, no global switch.

## Sketches

```csharp
// Addressables-style catalog initialization (session Content stage):
public sealed class CatalogContentSource : ContentSource<CatalogSnapshot>, IContentStartupInitializableService
{
    public override string SourceName => "addressables-catalog";
    public override bool IsOptional { get; protected init; } = true;   // offline-safe
    protected override CatalogSnapshot? FallbackData { get; } = CatalogSnapshot.BuiltIn();

    protected override async Task<CatalogSnapshot> LoadAsync(CancellationToken ct)
    {
        await UnityEngine.AddressableAssets.Addressables.InitializeAsync().AsTask();
        var handle = UnityEngine.AddressableAssets.Addressables.LoadResourceLocationsAsync("gameplay", typeof(UnityEngine.ResourceManagement.ResourceLocations.IResourceLocation));
        await handle.Task; // map locations into the snapshot
        return CatalogSnapshot.From(handle.Result);
    }
}

// Google Play Games sign-in (session Platform stage, user-gated dialog):
public sealed class PlayGamesAuthSource : ContentSource<AuthSnapshot>,
    IPlatformStartupInitializableService, IUserInteractionGatedInitializableService
{
    public override string SourceName => "play-games-auth";
    public override bool IsOptional { get; protected init; } = true;   // play without sign-in
    protected override AuthSnapshot? FallbackData { get; } = AuthSnapshot.Anonymous();

    protected override async Task<AuthSnapshot> LoadAsync(CancellationToken ct)
    {
        var task = PlayGamesPlatform.Instance.Authenticate(...);      // platform SDK call
        await task;                                                    // dialog may stay open; watchdog is exempt
        return AuthSnapshot.From(PlayGamesPlatform.Instance.GetUserId());
    }
}
```

The framework never references platform SDKs — these live in game code; the primitive lives
here. Any backend (Firebase, UGS Remote Config, custom HTTP) is just a different `LoadAsync`.

## In tests

Instance fakes participate like type-registered services, so overriding a source is one line:

```csharp
var fake = LifecycleFake.OfHandle<IContentSource<RemoteConfigSnapshot>>(
    new StaticContent<RemoteConfigSnapshot>(new RemoteConfigSnapshot { Environment = "test-env" }));

await using var app = await TestPipeline.Create()
    .Configure(b => b.Global().Content<ProdConfigSource, RemoteConfigSnapshot>())
    .OverrideInstance(fake.Service)
    .StartAsync();
```

Or implement `ContentSource<TData>` directly with an immediate snapshot for canned worlds.
