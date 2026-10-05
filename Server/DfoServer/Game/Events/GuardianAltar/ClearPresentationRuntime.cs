using System;

namespace DfoServer.Game.Events.GuardianAltar
{
    // Projection checkpoints over the existing Combat terminal fact. This
    // object never decides the battle result, commits rewards or stores wire
    // bodies. Callers still fence the current session/run around every write.
    internal sealed class GuardianAltarClearPresentationRuntime
    {
        private readonly object _syncRoot = new object();
        private GuardianAltarTerminalFact _fact;
        private GuardianAltarClearCommitResult _commitResult;
        private bool _survivalResult;
        private bool _inventoryProjectionSent;
        private bool _clearProjectionSent;
        private bool _eplpProjectionSent;
        private DateTime _eplpDeadlineUtc;
        private DateTime _returnDeadlineUtc;

        internal GuardianAltarTerminalFact Fact { get { lock (_syncRoot) return _fact; } }
        internal GuardianAltarClearCommitResult CommitResult { get { lock (_syncRoot) return _commitResult; } }
        internal bool InventoryProjectionSent { get { lock (_syncRoot) return _inventoryProjectionSent; } }
        internal bool ClearProjectionSent { get { lock (_syncRoot) return _clearProjectionSent; } }
        internal bool EplpProjectionSent { get { lock (_syncRoot) return _eplpProjectionSent; } }
        internal DateTime EplpDeadlineUtc { get { lock (_syncRoot) return _eplpDeadlineUtc; } }
        internal DateTime ReturnDeadlineUtc { get { lock (_syncRoot) return _returnDeadlineUtc; } }

        internal bool TryBind(GuardianAltarTerminalFact fact, bool survivalResult = false)
        {
            if (fact == null || fact.EventId == Guid.Empty || !fact.Identity.IsValid
                || fact.LoadingGeneration <= 0 || fact.ActiveMilliseconds < 0
                || fact.ClearedRounds < 0 || fact.ClearedRounds > short.MaxValue
                || (!survivalResult && fact.ClearedRounds != 0)
                || string.IsNullOrWhiteSpace(fact.Reason)
                || fact.Result is not (GuardianAltarTerminalResult.Succeeded or GuardianAltarTerminalResult.Failed))
                return false;
            lock (_syncRoot)
            {
                if (_fact != null)
                    return _fact == fact && _survivalResult == survivalResult;
                _fact = fact;
                _survivalResult = survivalResult;
                return true;
            }
        }

        internal bool TryCommit(GuardianAltarClearCommitResult result)
        {
            lock (_syncRoot)
            {
                if (_fact == null)
                    return false;
                if (_fact.Result == GuardianAltarTerminalResult.Failed && !_survivalResult)
                    return result == null;
                if (result == null || result.ProgressSnapshot == null || result.StarsGained < 0
                    || result.MailedRewardCount < 0 || result.Changes == null || result.NewPurchases == null)
                    return false;
                if (_survivalResult && (result.ClearedRounds != _fact.ClearedRounds
                    || result.BestRound < result.ClearedRounds || result.BestRound > short.MaxValue
                    || result.BestRound != result.ProgressSnapshot.Header.SurvivalBestPhase
                    || result.StarsGained != 0 || result.FirstClear || result.NewPurchases.Count != 0))
                    return false;
                if (_commitResult != null)
                    return ReferenceEquals(_commitResult, result);
                _commitResult = result;
                return true;
            }
        }

        internal bool TryMarkInventoryProjectionSent(Guid eventId)
        {
            lock (_syncRoot)
            {
                if (!CanProjectSuccessLocked(eventId))
                    return false;
                _inventoryProjectionSent = true;
                return true;
            }
        }

        internal bool TryMarkClearProjectionSent(Guid eventId, DateTime utcNow)
        {
            lock (_syncRoot)
            {
                if (!CanProjectSuccessLocked(eventId) || !_inventoryProjectionSent
                    || utcNow.Kind != DateTimeKind.Utc)
                    return false;
                if (_clearProjectionSent)
                    return true;
                if (!TryAddSeconds(utcNow, 4, out var deadline))
                    return false;
                _clearProjectionSent = true;
                _eplpDeadlineUtc = deadline;
                return true;
            }
        }

        internal bool TryMarkEplpProjectionSent(Guid eventId, DateTime utcNow)
        {
            lock (_syncRoot)
            {
                if (!MatchesLocked(eventId) || utcNow.Kind != DateTimeKind.Utc)
                    return false;
                if ((_fact.Result == GuardianAltarTerminalResult.Succeeded || _survivalResult)
                    && (!CanProjectSuccessLocked(eventId) || !_clearProjectionSent))
                    return false;
                if (_eplpProjectionSent)
                    return true;
                if ((_fact.Result == GuardianAltarTerminalResult.Succeeded || _survivalResult) && utcNow < _eplpDeadlineUtc)
                    return false;
                if (!TryAddSeconds(utcNow, 50, out var deadline))
                    return false;
                _eplpProjectionSent = true;
                _returnDeadlineUtc = deadline;
                return true;
            }
        }

        private bool MatchesLocked(Guid eventId)
            => _fact != null && eventId != Guid.Empty && _fact.EventId == eventId;

        private bool CanProjectSuccessLocked(Guid eventId)
            => MatchesLocked(eventId) && (_fact.Result == GuardianAltarTerminalResult.Succeeded || _survivalResult)
                && _commitResult != null;

        private static bool TryAddSeconds(DateTime utcNow, int seconds, out DateTime deadline)
        {
            try
            {
                deadline = utcNow.AddSeconds(seconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                deadline = default;
                return false;
            }
        }
    }
}
