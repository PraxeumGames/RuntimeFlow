using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Demo.Midcore
{
    // ===== shared =====

    public sealed class FakeBackend
    {
        public bool FailConfig { get; set; }
        public bool FailAuth { get; set; }
        public bool FailProfile { get; set; }
        private readonly object _sync = new();
        private readonly List<string> _log = new();
        public IReadOnlyList<string> Requests { get { lock (_sync) return _log.ToArray(); } }
        public void Reset() { FailConfig = FailAuth = false; lock (_sync) _log.Clear(); }
        public void Log(string r) { lock (_sync) _log.Add(r); }
        public int Count(string p) { lock (_sync) { var n = 0; foreach (var r in _log) if (r.StartsWith(p)) n++; return n; } }
        public async Task<T> GetAsync<T>(string ep, T result, bool fail, CancellationToken ct)
        {
            Log(ep);
            await Task.Delay(5, ct).ConfigureAwait(false);
            if (fail) throw new InvalidOperationException($"Backend '{ep}' unreachable.");
            return result;
        }
    }

    public static class PersistentState
    {
        public static int SaveVersion { get; set; }
        public static bool GdprAccepted { get; set; }
        public static void ResetAll() { SaveVersion = 0; GdprAccepted = false; }
    }

    // ===== snapshots =====

    public sealed class AuthData { public string PlayerId { get; set; } = "anon"; public bool SignedIn { get; set; } }

    // ===== Stage 1: GDPR consent =====

    public sealed class GdprConsentService : IGlobalInitializableService, IUserInteractionGatedInitializableService
    {
        public static bool Accepted { get; private set; }
        public static void ResetAccepted() => Accepted = false;
        public Task InitializeAsync(CancellationToken ct)
        {
            PersistentState.GdprAccepted = true;
            Accepted = true;
            return Task.CompletedTask;
        }
    }

    // ===== Stage 2: Save migration =====

    public sealed class SaveMigrationService : IGlobalInitializableService
    {
        public const int CurrentVersion = 3;
        public static int FromVersion { get; private set; } = -1;
        public static void ResetMigration() => FromVersion = -1;
        public Task InitializeAsync(CancellationToken ct)
        {
            FromVersion = PersistentState.SaveVersion;
            if (PersistentState.SaveVersion < CurrentVersion)
                PersistentState.SaveVersion = CurrentVersion;
            return Task.CompletedTask;
        }
    }

    // ===== Stage 3: Remote config =====

    public sealed class RemoteConfigService : IGlobalInitializableService
    {
        private readonly FakeBackend _backend;
        public static string Environment { get; private set; } = "unknown";
        public static int GiftCoins { get; private set; }
        public static void ResetConfig() { Environment = "unknown"; GiftCoins = 0; }
        public RemoteConfigService(FakeBackend backend) => _backend = backend;
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            var config = await _backend.GetAsync("GET /remote-config",
                new RemoteConfigSnapshot { Env = "prod", GiftCoins = 250 },
                _backend.FailConfig, cancellationToken).ConfigureAwait(false);
            Environment = config.Env;
            GiftCoins = config.GiftCoins;
        }
    }

    public sealed class RemoteConfigSnapshot { public string Env { get; set; } = "dev"; public int GiftCoins { get; set; } }

    // ===== Stage 4: Platform auth =====

    public sealed class PlatformAuthService : IGlobalInitializableService, IUserInteractionGatedInitializableService
    {
        private readonly FakeBackend _backend;
        public static string PlayerId { get; private set; } = "anonymous";
        public static bool SignedIn { get; private set; }
        public static void ResetAuth() { PlayerId = "anonymous"; SignedIn = false; }
        public PlatformAuthService(FakeBackend backend) => _backend = backend;
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var auth = await _backend.GetAsync("POST /gpg/auth",
                    new AuthData { PlayerId = "gpg-777", SignedIn = true },
                    _backend.FailAuth, cancellationToken).ConfigureAwait(false);
                PlayerId = auth.PlayerId;
                SignedIn = auth.SignedIn;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Degrade to anonymous: game is playable without sign-in.
                PlayerId = "anonymous";
                SignedIn = false;
            }
        }
    }

    // ===== Session: Server profile =====

    public sealed class ServerProfileService : ISessionInitializableService
    {
        private readonly FakeBackend _backend;
        public static string DisplayName { get; private set; } = "OfflineWarrior";
        public static int Level { get; private set; } = 1;
        public static bool LoadedFromServer { get; private set; }
        public static void ResetProfile() { DisplayName = "OfflineWarrior"; LoadedFromServer = false; }
        public ServerProfileService(FakeBackend backend) => _backend = backend;
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var profile = await _backend.GetAsync("GET /profile",
                    new ProfileData { DisplayName = $"Hero_{PlatformAuthService.PlayerId}", Level = 42 },
                    _backend.FailProfile, cancellationToken).ConfigureAwait(false);
                DisplayName = profile.DisplayName;
                Level = profile.Level;
                LoadedFromServer = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Degrade to offline profile.
                DisplayName = "OfflineWarrior";
                Level = 1;
                LoadedFromServer = false;
            }
        }
    }

    public sealed class ProfileData { public string DisplayName { get; set; } = "Guest"; public int Level { get; set; } = 1; }
}
