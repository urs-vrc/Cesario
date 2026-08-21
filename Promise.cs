// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;
using UnityEngine;

namespace Cesario
{
    /// <summary>
    /// A <c>Promise</c> is an object that represents the eventual completion or failure
    /// of an asynchronous operation. It is similar to a Future in Dart/Java/Kotlin/etc.
    ///
    /// Promises are a state machine with three states — pending, fulfilled and rejected.
    /// The eventual state of a promise can be fulfilled or rejected; handlers are queued
    /// via <see cref="Then"/>, <see cref="Catch"/> and <see cref="Chain"/>.
    ///
    /// Callback dispatch is never synchronous. Settlement hands work off to a
    /// <see cref="PromiseScheduler"/>, which defers actual invocation by a frame.
    /// See <see cref="PromiseScheduler"/> for why.
    ///
    /// U# does not support custom interfaces, so all callback dispatch goes through
    /// <c>SendCustomEvent</c> + <c>SetProgramVariable</c>/<c>GetProgramVariable</c>
    /// string-name conventions rather than typed method calls. Every callback target
    /// must expose a public <c>Promise IncomingPromise</c> field; Chain targets
    /// additionally need a public <c>object ChainResult</c> (and optionally
    /// <c>Promise ChainResultAdoptee</c>) field.
    ///
    /// https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Promise
    /// https://promisesaplus.com/
    /// </summary>
    public class Promise : UdonSharpBehaviour
    {
        [System.NonSerialized] public PromiseState State = PromiseState.Pending;
        [System.NonSerialized] public object Value;
        [System.NonSerialized] public PromiseScheduler Scheduler;
        [System.NonSerialized] public PromiseFactory Factory;

        /// <summary>
        /// Read-back slot for this Promise's own Then/Catch subscriptions
        /// (used internally for thenable adoption). Same contract every callback
        /// target follows.
        /// </summary>
        [System.NonSerialized] public Promise IncomingPromise;

        private UdonSharpBehaviour[] _fulfillTargets = new UdonSharpBehaviour[8];
        private string[] _fulfillEvents = new string[8];
        private int _fulfillCount = 0;

        private UdonSharpBehaviour[] _rejectTargets = new UdonSharpBehaviour[8];
        private string[] _rejectEvents = new string[8];
        private int _rejectCount = 0;

        private UdonSharpBehaviour[] _chainTargets = new UdonSharpBehaviour[4];
        private string[] _chainFulfillEvents = new string[4];
        private string[] _chainRejectEvents = new string[4];
        private Promise[] _chainNext = new Promise[4];
        private int _chainCount = 0;

        private bool _adopting = false;

        /// <summary>
        /// Registers a handler that runs when this promise fulfills.
        /// Fire-and-forget — does not return a new promise. Use <see cref="Chain"/>
        /// when you need a continuation.
        /// </summary>
        public void Then(UdonSharpBehaviour target, string onFulfilledEvent)
        {
            if (_fulfillCount >= _fulfillTargets.Length) GrowFulfill();
            _fulfillTargets[_fulfillCount] = target;
            _fulfillEvents[_fulfillCount] = onFulfilledEvent;
            _fulfillCount++;

            if (State == PromiseState.Fulfilled && HasScheduler())
                Scheduler.Enqueue(target, onFulfilledEvent, this);
        }

        /// <summary>
        /// Registers a handler that runs when this promise rejects.
        /// Fire-and-forget — does not return a new promise.
        /// </summary>
        public void Catch(UdonSharpBehaviour target, string onRejectedEvent)
        {
            if (_rejectCount >= _rejectTargets.Length) GrowReject();
            _rejectTargets[_rejectCount] = target;
            _rejectEvents[_rejectCount] = onRejectedEvent;
            _rejectCount++;

            if (State == PromiseState.Rejected && HasScheduler())
                Scheduler.Enqueue(target, onRejectedEvent, this);
        }

        /// <summary>
        /// Registers a chained handler pair and returns a new Promise settled by
        /// whichever event fires. The firing handler must write its result into
        /// <c>target.ChainResult</c> before its event method returns — the scheduler
        /// reads it back afterward and resolves the returned promise with it.
        /// Returning a Promise via <c>ChainResultAdoptee</c> causes the next promise
        /// to adopt it instead of settling immediately.
        /// </summary>
        public Promise Chain(UdonSharpBehaviour target, string onFulfilledEvent, string onRejectedEvent)
        {
            if (Factory == null)
            {
                Debug.LogError($"[Promise] '{name}' has no Factory assigned - cannot Chain.");
                return null;
            }

            var next = Factory.Create();
            if (next == null) return null;

            if (_chainCount >= _chainTargets.Length) GrowChain();
            _chainTargets[_chainCount] = target;
            _chainFulfillEvents[_chainCount] = onFulfilledEvent;
            _chainRejectEvents[_chainCount] = onRejectedEvent;
            _chainNext[_chainCount] = next;
            _chainCount++;

            if (IsSettled() && HasScheduler())
                Scheduler.EnqueueChain(target, onFulfilledEvent, onRejectedEvent, this, next);

            return next;
        }

        /// <summary>
        /// Settles this promise as fulfilled with the given value. A no-op if the
        /// promise has already settled (fulfilled or rejected) — per spec, a promise
        /// may only transition out of pending once.
        /// </summary>
        public void Resolve(object value)
        {
            if (State != PromiseState.Pending || _adopting) return;

            State = PromiseState.Fulfilled;
            Value = value;

            if (!HasScheduler()) return;
            DispatchAll();
        }

        /// <summary>
        /// Settles this promise by waiting on another Promise instead of a plain
        /// value. The explicit alternative to detecting "is value a Promise" inside
        /// <see cref="Resolve"/> — U#'s compiler crashes on <c>value as Promise</c>
        /// type-checks, so callers that know they have a Promise-typed result call
        /// this directly instead of <see cref="Resolve"/>.
        ///
        /// Per Promises/A+ 2.3.1, adopting oneself is rejected with a TypeError-style
        /// reason rather than allowed to proceed - without this guard, a promise
        /// adopted with itself would sit adopting forever (subscribing to its own
        /// Then/Catch, which can never fire since it never leaves the pending state).
        /// </summary>
        public void Adopt(Promise inner)
        {
            if (State != PromiseState.Pending || _adopting) return;

            if (inner == this)
            {
                Reject("TypeError: Promise adopted with itself (self-resolution is not allowed)");
                return;
            }

            if (inner == null)
            {
                Resolve(null);
                return;
            }

            _adopting = true;
            inner.Then(this, nameof(OnAdopteeFulfilled));
            inner.Catch(this, nameof(OnAdopteeRejected));
        }

        /// <summary>
        /// Settles this promise as rejected with the given reason. A no-op if already settled.
        /// </summary>
        public void Reject(object reason)
        {
            if (State != PromiseState.Pending || _adopting) return;

            State = PromiseState.Rejected;
            Value = reason;

            if (_fulfillCount == 0 && _rejectCount == 0 && _chainCount == 0)
                Debug.LogWarning($"[Promise] Unhandled rejection on '{name}': {reason}");

            if (!HasScheduler()) return;
            DispatchAll();
        }

        /// <summary>
        /// Returns true if the promise is either fulfilled or rejected.
        /// </summary>
        public bool IsSettled()
        {
            return State != PromiseState.Pending;
        }

        /// <summary>
        /// Returns true if the promise is still pending.
        /// </summary>
        public bool IsPending()
        {
            return State == PromiseState.Pending;
        }

        /// <summary>
        /// Returns true if the promise has fulfilled.
        /// </summary>
        public bool IsFulfilled()
        {
            return State == PromiseState.Fulfilled;
        }

        /// <summary>
        /// Returns true if the promise has rejected.
        /// </summary>
        public bool IsRejected()
        {
            return State == PromiseState.Rejected;
        }
        
        #region Pooling

        /// <summary>
        /// Resets the promise so it can be returned to the pool and reused.
        /// Called by <see cref="PromiseFactory"/> before handing it out again.
        /// </summary>
        public void ResetState()
        {
            State = PromiseState.Pending;
            Value = null;
            _fulfillCount = 0;
            _rejectCount = 0;
            _chainCount = 0;
            _adopting = false;
            IncomingPromise = null;

            // Clear references so we don't keep dead behaviours alive
            for (int i = 0; i < _fulfillTargets.Length; i++) _fulfillTargets[i] = null;
            for (int i = 0; i < _rejectTargets.Length; i++) _rejectTargets[i] = null;
            for (int i = 0; i < _chainTargets.Length; i++)
            {
                _chainTargets[i] = null;
                _chainNext[i] = null;
            }
        }
        
        #endregion
        
        #region Internal adoption handlers

        /// <summary>
        /// Internal adoption handlers — this Promise is itself a Then/Catch target
        /// of the inner Promise it's waiting on, so it follows the same
        /// <c>IncomingPromise</c> read-back convention as everyone else.
        /// </summary>
        public void OnAdopteeFulfilled()
        {
            _adopting = false;
            Resolve(IncomingPromise.Value);
        }

        public void OnAdopteeRejected()
        {
            _adopting = false;
            Reject(IncomingPromise.Value);
        }
        
        #endregion

        #region Internals

        private void DispatchAll()
        {
            if (State == PromiseState.Fulfilled)
            {
                for (int i = 0; i < _fulfillCount; i++)
                    Scheduler.Enqueue(_fulfillTargets[i], _fulfillEvents[i], this);
            }
            else
            {
                for (int i = 0; i < _rejectCount; i++)
                    Scheduler.Enqueue(_rejectTargets[i], _rejectEvents[i], this);
            }

            for (int i = 0; i < _chainCount; i++)
                Scheduler.EnqueueChain(_chainTargets[i], _chainFulfillEvents[i], _chainRejectEvents[i], this, _chainNext[i]);
        }

        private bool HasScheduler()
        {
            if (Scheduler != null) return true;
            Debug.LogError($"[Promise] '{name}' has no Scheduler assigned - callback dispatch will fail.");
            return false;
        }

        private void GrowFulfill()
        {
            var newTargets = new UdonSharpBehaviour[_fulfillTargets.Length * 2];
            var newEvents = new string[_fulfillEvents.Length * 2];
            _fulfillTargets.CopyTo(newTargets, 0);
            _fulfillEvents.CopyTo(newEvents, 0);
            _fulfillTargets = newTargets;
            _fulfillEvents = newEvents;
        }

        private void GrowReject()
        {
            var newTargets = new UdonSharpBehaviour[_rejectTargets.Length * 2];
            var newEvents = new string[_rejectEvents.Length * 2];
            _rejectTargets.CopyTo(newTargets, 0);
            _rejectEvents.CopyTo(newEvents, 0);
            _rejectTargets = newTargets;
            _rejectEvents = newEvents;
        }

        private void GrowChain()
        {
            var newTargets = new UdonSharpBehaviour[_chainTargets.Length * 2];
            var newFulfill = new string[_chainFulfillEvents.Length * 2];
            var newReject = new string[_chainRejectEvents.Length * 2];
            var newNext = new Promise[_chainNext.Length * 2];
            _chainTargets.CopyTo(newTargets, 0);
            _chainFulfillEvents.CopyTo(newFulfill, 0);
            _chainRejectEvents.CopyTo(newReject, 0);
            _chainNext.CopyTo(newNext, 0);
            _chainTargets = newTargets;
            _chainFulfillEvents = newFulfill;
            _chainRejectEvents = newReject;
            _chainNext = newNext;
        }
        
        #endregion
    }
}