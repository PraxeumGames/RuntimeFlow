using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextScopeProfileStore
    {
        private readonly List<Action<IGameContext>> _globalRegistrations = new();
        private readonly List<Action<IGameContext>> _sessionRegistrations = new();
        private readonly Dictionary<Type, ScopeProfile> _sceneProfiles = new();
        private readonly Dictionary<Type, ScopeProfile> _moduleProfiles = new();

        public IReadOnlyCollection<Action<IGameContext>> GlobalRegistrations => _globalRegistrations;
        public IReadOnlyCollection<Action<IGameContext>> SessionRegistrations => _sessionRegistrations;
        public bool HasGlobalRegistrations => _globalRegistrations.Count > 0;

        public bool HasSceneProfile(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _sceneProfiles.ContainsKey(scopeKey);
        }

        public bool HasModuleProfile(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _moduleProfiles.ContainsKey(scopeKey);
        }

        public ScopeProfile GetSceneProfile(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _sceneProfiles[scopeKey];
        }

        public ScopeProfile GetModuleProfile(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _moduleProfiles[scopeKey];
        }

        public bool TryGetSceneProfile(Type scopeKey, out ScopeProfile profile)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _sceneProfiles.TryGetValue(scopeKey, out profile!);
        }

        public bool TryGetModuleProfile(Type scopeKey, out ScopeProfile profile)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _moduleProfiles.TryGetValue(scopeKey, out profile!);
        }

        public void BindScopedRegistration(
            GameContextType scope,
            Type? scopeKey,
            Action<IGameContext> registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));

            switch (scope)
            {
                case GameContextType.Global:
                    _globalRegistrations.Add(registration);
                    break;
                case GameContextType.Session:
                    _sessionRegistrations.Add(registration);
                    break;
                case GameContextType.Scene:
                    if (scopeKey == null)
                        throw new ArgumentNullException(nameof(scopeKey), "Scene scope key is required. Use the typed ConfigureScene<TScope>() API.");
                    GetOrCreateProfile(_sceneProfiles, scopeKey).Registrations.Add(registration);
                    break;
                case GameContextType.Module:
                    if (scopeKey == null)
                        throw new ArgumentNullException(nameof(scopeKey), "Module scope key is required. Use the typed ConfigureModule<TScope>() API.");
                    GetOrCreateProfile(_moduleProfiles, scopeKey).Registrations.Add(registration);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope.");
            }
        }

        private static ScopeProfile GetOrCreateProfile(IDictionary<Type, ScopeProfile> profiles, Type scopeKey)
        {
            if (!profiles.TryGetValue(scopeKey, out var profile))
            {
                profile = new ScopeProfile();
                profiles[scopeKey] = profile;
            }

            return profile;
        }
    }
}
