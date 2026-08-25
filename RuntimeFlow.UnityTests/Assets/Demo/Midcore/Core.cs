using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Demo.Midcore
{
    // ---------- snapshots ----------

    public sealed class RemoteConfigData { public string Env { get; set; } = "dev"; public int GiftCoins { get; set; } public bool FeatureDailyRewards { get; set; } }
    public sealed class AuthData { public string PlayerId { get; set; } = "anonymous"; public bool SignedIn { get; set; } }
    public sealed class ProfileData { public string DisplayName { get; set; } = "Guest"; public int Level { get; set; } = 1; public int Coins { get; set; } }
    public sealed class CatalogData { public List<string> Bundles { get; set; } = new(); }

    // ---------- backend simulator ----------

    public sealed class FakeBackend
    {
        public bool FailConfig { get; set; }
        public bool FailAuth { get; set; }
        public bool FailProfile { get; set; }

        private readonly object _sync = new();
        private readonly List<string> _log = new();
        public IReadOnlyList<string> Requests { get { lock (_sync) return _log.ToArray(); } }
        public void Reset() { FailConfig = FailAuth = FailProfile = false; lock (_sync) _log.Clear(); }
        public void Log(string r) { lock (_sync) _log.Add(r); }
        public int Count(string prefix) { lock (_sync) { var n = 0; foreach (var r in _log) if (r.StartsWith(prefix)) n++; return n; } }

        public async Task<T> GetAsync<T>(string endpoint, T result, bool fail, CancellationToken ct)
        {
            Log(endpoint);
            await Task.Delay(5, ct).ConfigureAwait(false);
            if (fail) throw new InvalidOperationException($"Backend '{endpoint}' unreachable.");
            return result;
        }
    }

    // ---------- content sources ----------
    // ALL in Global scope (IGlobalInitializableService): loaded once per app launch,
    // guaranteed ready before any Session-scope consumer starts.

    /// <summary>Remote config: REQUIRED. Determines economy values and feature flags.</summary>
    public sealed class RemoteConfigContent : ContentSource<RemoteConfigData>, IGlobalInitializableService
    {
        private readonly FakeBackend _backend;
        public RemoteConfigContent(FakeBackend backend) => _backend = backend;

        public override string SourceName => "remote-config";
        protected override async Task<RemoteConfigData> LoadAsync(CancellationToken cancellationToken)
        {
            return await _backend.GetAsync(
                "GET /remote-config",
                new RemoteConfigData { Env = "prod", GiftCoins = 250, FeatureDailyRewards = true },
                _backend.FailConfig,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Google Play Games sign-in: OPTIONAL (playable anonymously), USER-GATED
    /// (consent dialog exempt from health watchdog).
    /// </summary>
    public sealed class AuthContent : ContentSource<AuthData>, IGlobalInitializableService, IUserInteractionGatedInitializableService
    {
        private readonly FakeBackend _backend;

        public AuthContent(FakeBackend backend)
        {
            _backend = backend;
            Policy(optional: true, fallback: new AuthData { PlayerId = "anonymous", SignedIn = false });
        }

        public override string SourceName => "gpg-sign-in";

        protected override async Task<AuthData> LoadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);   // simulated consent dialog
            return await _backend.GetAsync(
                "POST /gpg/auth",
                new AuthData { PlayerId = "gpg-777", SignedIn = true },
                _backend.FailAuth,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
