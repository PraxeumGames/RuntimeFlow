using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>Everything the demo backend knows about the player's remote configuration.</summary>
    public sealed class RemoteConfig
    {
        /// <summary>True while the operators closed the game; <see cref="MaintenanceGateService"/> halts on it.</summary>
        public bool Maintenance { get; set; }

        /// <summary>Coins granted on top of the stored profile, so the profile visibly depends on the config.</summary>
        public int GiftCoins { get; set; }

        /// <summary>Catalog revision the client is expected to load.</summary>
        public string CatalogVersion { get; set; } = "unknown";
    }

    /// <summary>Result of a platform sign-in.</summary>
    public sealed class AuthResult
    {
        /// <summary>Stable platform identifier of the signed-in player.</summary>
        public string PlayerId { get; set; } = "anonymous";
    }

    /// <summary>The player's stored progression.</summary>
    public sealed class PlayerProfile
    {
        /// <summary>Owner of the profile; filled in from the auth result.</summary>
        public string PlayerId { get; set; } = "anonymous";

        /// <summary>Name shown in the UI.</summary>
        public string DisplayName { get; set; } = "Guest";

        /// <summary>Account level.</summary>
        public int Level { get; set; }

        /// <summary>Soft currency, gift coins from the remote config included.</summary>
        public int Coins { get; set; }
    }

    /// <summary>The item catalog of one content revision.</summary>
    public sealed class Catalog
    {
        /// <summary>Revision the backend served.</summary>
        public string Version { get; set; } = "unknown";

        /// <summary>Number of items in this revision.</summary>
        public int ItemCount { get; set; }
    }

    /// <summary>The quests the warmup step prepares.</summary>
    public sealed class QuestPack
    {
        /// <summary>Revision the quests belong to.</summary>
        public string Version { get; set; } = "unknown";

        /// <summary>Number of quests to warm up.</summary>
        public int QuestCount { get; set; }
    }

    /// <summary>
    /// An in-memory stand-in for the game's HTTP backend: five endpoints with their own latency, a
    /// request log, and per-endpoint failure injection (<see cref="Fail"/>, <see cref="Hang"/>,
    /// <see cref="Slow"/>). Everything runs on the Unity main thread, so there are no locks.
    /// </summary>
    public sealed class FakeBackend
    {
        /// <summary>The endpoint keys the demo services call.</summary>
        public static class Endpoints
        {
            /// <summary>Remote configuration, fetched by the global scope.</summary>
            public const string Config = "/config";

            /// <summary>Platform sign-in, fetched by the global scope.</summary>
            public const string Auth = "/auth";

            /// <summary>The player's stored progression.</summary>
            public const string Profile = "/profile";

            /// <summary>The item catalog of the configured revision.</summary>
            public const string Catalog = "/catalog";

            /// <summary>The quest pack warmed up after the catalog.</summary>
            public const string Quests = "/quests";

            /// <summary>Every endpoint, in the order the demo calls them.</summary>
            public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
                new[] { Config, Auth, Profile, Catalog, Quests });
        }

        private readonly Dictionary<string, EndpointState> _endpoints = new Dictionary<string, EndpointState>(StringComparer.Ordinal);
        private readonly List<string> _requests = new List<string>();

        /// <summary>Creates a healthy backend where every endpoint answers after a few milliseconds.</summary>
        public FakeBackend()
        {
            foreach (var endpoint in Endpoints.All) _endpoints[endpoint] = new EndpointState();
        }

        /// <summary>Value the <c>/config</c> endpoint reports as <see cref="RemoteConfig.Maintenance"/>.</summary>
        public bool Maintenance { get; set; }

        /// <summary>Value the <c>/config</c> endpoint reports as <see cref="RemoteConfig.GiftCoins"/>.</summary>
        public int GiftCoins { get; set; } = 250;

        /// <summary>Revision reported by <c>/config</c> and served by <c>/catalog</c> and <c>/quests</c>.</summary>
        public string CatalogVersion { get; set; } = "2026.09.1";

        /// <summary>Identifier <c>/auth</c> hands back on a successful sign-in.</summary>
        public string PlayerId { get; set; } = "gpg-777";

        /// <summary>Every endpoint that was called, in order, including calls that then failed.</summary>
        public IReadOnlyList<string> Requests => _requests;

        /// <summary>How many times <paramref name="endpoint"/> was called.</summary>
        public int CountOf(string endpoint)
        {
            var count = 0;
            for (var i = 0; i < _requests.Count; i++)
            {
                if (string.Equals(_requests[i], endpoint, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        /// <summary>Makes <paramref name="endpoint"/> throw instead of answering.</summary>
        public void Fail(string endpoint) => State(endpoint).Fails = true;

        /// <summary>Makes <paramref name="endpoint"/> never answer; the call awaits the token forever.</summary>
        public void Hang(string endpoint) => State(endpoint).Hangs = true;

        /// <summary>Sets the latency of <paramref name="endpoint"/> in milliseconds.</summary>
        public void Slow(string endpoint, int milliseconds) => State(endpoint).LatencyMilliseconds = milliseconds;

        /// <summary>Restores <paramref name="endpoint"/> to its healthy default.</summary>
        public void Heal(string endpoint) => _endpoints[Known(endpoint)] = new EndpointState();

        /// <summary>Forgets every logged request; the injected failures stay.</summary>
        public void ClearRequests() => _requests.Clear();

        /// <summary>
        /// Calls <paramref name="endpoint"/> and returns its typed payload after the endpoint's latency.
        /// </summary>
        /// <exception cref="InvalidOperationException">The endpoint is set to fail, or <typeparamref name="T"/> is not its payload type.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled while waiting.</exception>
        public async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken) where T : class
        {
            var state = State(endpoint);
            _requests.Add(endpoint);

            if (state.Hangs) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (state.LatencyMilliseconds > 0) await Task.Delay(state.LatencyMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (state.Fails)
                throw new InvalidOperationException($"Backend endpoint '{endpoint}' is unreachable.");

            var payload = Payload(endpoint);
            if (payload is T typed) return typed;
            throw new InvalidOperationException(
                $"Endpoint '{endpoint}' answers with {payload.GetType().Name}, not {typeof(T).Name}.");
        }

        private object Payload(string endpoint)
        {
            switch (endpoint)
            {
                case Endpoints.Config:
                    return new RemoteConfig { Maintenance = Maintenance, GiftCoins = GiftCoins, CatalogVersion = CatalogVersion };
                case Endpoints.Auth:
                    return new AuthResult { PlayerId = PlayerId };
                case Endpoints.Profile:
                    return new PlayerProfile { DisplayName = "Guest", Level = 42, Coins = 100 };
                case Endpoints.Catalog:
                    return new Catalog { Version = CatalogVersion, ItemCount = 128 };
                case Endpoints.Quests:
                    return new QuestPack { Version = CatalogVersion, QuestCount = 12 };
                default:
                    throw new ArgumentException($"Unknown endpoint '{endpoint}'.", nameof(endpoint));
            }
        }

        private EndpointState State(string endpoint) => _endpoints[Known(endpoint)];

        private string Known(string endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (!_endpoints.ContainsKey(endpoint))
            {
                throw new ArgumentException(
                    $"Unknown endpoint '{endpoint}'; the backend serves {string.Join(", ", Endpoints.All)}.",
                    nameof(endpoint));
            }
            return endpoint;
        }

        private sealed class EndpointState
        {
            public int LatencyMilliseconds = 3;
            public bool Fails;
            public bool Hangs;
        }
    }
}
