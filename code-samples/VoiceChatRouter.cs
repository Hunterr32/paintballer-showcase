// VoiceChatRouter — translates game state into VoiceChatManager calls.
//
// This is the ONLY place that reads the game's networking state for voice. It
// keeps VoiceChatManager (the Vivox pipe) in sync with:
//   • lobby vs. live match   → LobbyManager.MatchLive
//   • the local player's team → Health.GetCombatTeam(local connection)
//   • alive vs. dead          → Health.OnDiedVisual / OnRespawnedVisual
//
// It reads only existing PUBLIC API (PlayerController.Local, LobbyManager,
// MatchManager, Health), so no edits to the match/lobby code are required. All
// calls into VoiceChatManager are safe no-ops until the Vivox package + the
// VIVOX_PRESENT define are added, so this component is harmless before setup.

using UnityEngine;
using UnityEngine.InputSystem;

public class VoiceChatRouter : MonoBehaviour {
    // Change-tracking so we only push to VoiceChatManager on real transitions.
    private VoicePhase _pushedPhase = VoicePhase.None;
    private Team _pushedTeam = Team.None;
    private bool _pushedDead;
    private string _pushedSession = "";

    private PlayerController _boundLocal;
    private Health _boundHealth;
    private bool _loginTried;

    private void Update() {
        var vcm = VoiceChatManager.Instance;
        if (vcm == null) return;

        TryLogin(vcm);
        BindLocalPlayer();
        Evaluate(vcm);
        ApplyMicState(vcm);
    }

    // ── Master toggle sync + push-to-talk / open-mic enforcement ───────────
    private int _lastMuted = -1;   // -1 = unset, 0 = transmitting, 1 = muted

    private void ApplyMicState(VoiceChatManager vcm) {
        // Keep the manager's master switch in step with the settings toggle.
        bool enabled = GameplaySettings.VoiceChatEnabled;
        if (enabled != vcm.VoiceEnabled) vcm.SetVoiceEnabled(enabled);
        if (!enabled) return;

        bool muted = GameplaySettings.VoicePushToTalk ? !PushToTalkHeld() : false;
        int want = muted ? 1 : 0;
        if (want != _lastMuted) {
            vcm.SetInputMuted(muted);
            _lastMuted = want;
        }
    }

    private bool PushToTalkHeld() {
        if (!Application.isFocused) return false;
        if (InputManager.Instance != null && InputManager.Instance.UsingGamepad) {
            // Use the configured gamepad PTT button; if none is bound, transmit openly.
            int b = GameplaySettings.VoiceGamepadPttButton;
            var gp = Gamepad.current;
            if (b < 0 || gp == null) return true;
            return gp[(UnityEngine.InputSystem.LowLevel.GamepadButton)b].isPressed;
        }
        var kb = Keyboard.current;
        return kb != null && kb[GameplaySettings.VoicePushToTalkKey].isPressed;
    }

    private void OnDisable() {
        UnbindHealth();
    }

    // ── Vivox login (once we know who we are) ──────────────────────────────
    private void TryLogin(VoiceChatManager vcm) {
        // Disconnected: allow a fresh attempt on the next connection if a prior
        // login never succeeded (e.g. transient network / Vivox hiccup).
        if (LobbyManager.Instance == null) {
            if (!vcm.IsLoggedIn) _loginTried = false;
            return;
        }
        if (_loginTried || vcm.IsLoggedIn) return;

        // Log in with our LOBBY ROSTER NAME — the exact string the scoreboard/roster
        // show and mute by, so per-player mute matches the Vivox participant. Wait
        // until our own roster entry has replicated so the name is authoritative.
        string name = LocalMemberName();
        if (string.IsNullOrEmpty(name)) return;
        _loginTried = true;
        vcm.Login(name);
    }

    // ── Bind to the local player body for death/respawn events ─────────────
    private void BindLocalPlayer() {
        var local = PlayerController.Local;
        if (local == _boundLocal) return;

        UnbindHealth();
        _boundLocal = local;

        if (local != null) {
            _boundHealth = local.GetComponent<Health>();
            if (_boundHealth == null) _boundHealth = local.GetComponentInChildren<Health>();
            if (_boundHealth != null) {
                _boundHealth.OnDiedVisual += OnLocalDied;
                _boundHealth.OnRespawnedVisual += OnLocalRespawned;
            }
        }
    }

    private void UnbindHealth() {
        if (_boundHealth != null) {
            _boundHealth.OnDiedVisual -= OnLocalDied;
            _boundHealth.OnRespawnedVisual -= OnLocalRespawned;
            _boundHealth = null;
        }
        _boundLocal = null;
    }

    private void OnLocalDied() { VoiceChatManager.Instance?.SetDead(true); }
    private void OnLocalRespawned() { VoiceChatManager.Instance?.SetDead(false); }

    // ── Core: compute desired state, push only on change ───────────────────
    private void Evaluate(VoiceChatManager vcm) {
        VoicePhase phase = CurrentPhase();
        string session = CurrentSession();
        Team team = CurrentTeam();
        bool dead = CurrentDead();

        bool sessionChanged = session != _pushedSession;
        bool phaseChanged = phase != _pushedPhase;

        if (phaseChanged || sessionChanged) {
            switch (phase) {
                case VoicePhase.Lobby: vcm.EnterLobby(session); break;
                case VoicePhase.InMatch: vcm.EnterMatch(session, team, dead); break;
                default: vcm.LeaveAll(); break;
            }
            _pushedPhase = phase;
            _pushedSession = session;
            _pushedTeam = team;
            _pushedDead = dead;
            return;
        }

        // Same phase/session: push incremental team/dead changes.
        if (phase == VoicePhase.InMatch) {
            if (team != _pushedTeam) { vcm.SetTeam(team); _pushedTeam = team; }
            if (dead != _pushedDead) { vcm.SetDead(dead); _pushedDead = dead; }
        }
    }

    private VoicePhase CurrentPhase() {
        var lobby = LobbyManager.Instance;
        if (lobby == null) return VoicePhase.None;
        return lobby.MatchLive ? VoicePhase.InMatch : VoicePhase.Lobby;
    }

    private Team CurrentTeam() {
        // Owner of the local body is the local network connection.
        if (_boundLocal == null || _boundLocal.Owner == null) return Team.None;
        return Health.GetCombatTeam(_boundLocal.Owner);
    }

    private bool CurrentDead() {
        return _boundHealth != null && _boundHealth.IsDead;
    }

    // Session id must be IDENTICAL across all clients of one host, and UNIQUE
    // per host (so two separate games on the same Vivox project don't cross-talk).
    // The Steam lobby id satisfies both; fall back to a constant for direct connects.
    private string CurrentSession() {
#if !DISABLE_STEAMWORKS
        var steam = SteamLobbyManager.Instance;
        if (steam != null && steam.CurrentLobbyId != 0UL)
            return steam.CurrentLobbyId.ToString();
#endif
        return "main";
    }

    // Our own name as stored in the lobby roster — the identity remote players see
    // and mute by. Null until our roster entry has replicated to this client.
    private string LocalMemberName() {
        var lobby = LobbyManager.Instance;
        if (lobby == null) return null;
        int id = lobby.LocalClientId;
        foreach (var m in lobby.Members)
            if (m.ClientId == id && !string.IsNullOrEmpty(m.Name)) return m.Name;
        return null;
    }
}
