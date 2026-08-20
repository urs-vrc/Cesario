// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;


namespace Cesario
{
    /// <summary>
    /// A `Promise` is an object that represents an eventual completion of failure of an asynchronous operation
    /// It is similar to a Future in Dart/Java/Kotlin/etc, as they represent a value that is yet to be resolved.
    ///
    /// Promises are a state machine with three states - pending, fulfilled and rejected. The eventual state of
    /// a promise can be fulfilled or rejected, the following handlers are queued up from a .then method.
    ///
    /// Callback dispatch is never synchronous - Then/Catch/Chain registration and Resolve/Reject
    /// settlement both hand off to a `PromiseScheduler`, which defers actual invocation by
    /// a frame. See <see cref="PromiseScheduler"/> for why.
    ///
    /// U# does not support custom interfaces, so all callback dispatch here goes through
    /// SendCustomEvent + SetProgramVariable/GetProgramVariable string-name conventions
    /// rather than typed method calls. Every callback target must expose a public
    /// `Promise IncomingPromise` field; Chain targets additionally need a public
    /// `object ChainResult` field.
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

        /// <summary>Read-back slot for this Promise's own Then/Catch subscriptions
        /// (used internally for thenable adoption). Same contract every callback
        /// target follows.</summary>
        [System.NonSerialized] public Promise IncomingPromise;

        private UdonSharpBehaviour[] _fulfillTargets = new UdonSharpBehaviour[4];
        private string[] _fulfillEvents = new string[4];
        private int _fulfillCount = 0;

        private UdonSharpBehaviour[] _rejectTargets = new UdonSharpBehaviour[4];
        private string[] _rejectEvents = new string[4];
        private int _rejectCount = 0;

        private UdonSharpBehaviour[] _chainTargets = new UdonSharpBehaviour[4];
        private string[] _chainFulfillEvents = new string[4];
        private string[] _chainRejectEvents = new string[4];
        private Promise[] _chainNext = new Promise[4];
        private int _chainCount = 0;

        private bool _adopting = false;

        public void Then(UdonSharpBehaviour target, string onFulfilledEvent)
        {
            if (_fulfillCount >= _fulfillTargets.Length) GrowFulfill();
            _fulfillTargets[_fulfillCount] = target;
            _fulfillEvents[_fulfillCount] = onFulfilledEvent;
            _fulfillCount++;

            if (State == PromiseState.Fulfilled && HasScheduler())
                Scheduler.Enqueue(target, onFulfilledEvent, this);
        }

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
        /// `target.ChainResult` before its event method returns - the scheduler reads
        /// it back afterward and resolves the returned promise with it. Returning a
        /// Promise via ChainResult causes the next promise to adopt it instead of
        /// settling immediately.
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
        /// promise has already settled (fulfilled or rejected) - per spec, a promise
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
        /// Resolve() - U#'s compiler crashes on `value as Promise` type-checks, so
        /// callers that know they have a Promise-typed result call this directly
        /// instead of Resolve().
        /// </summary>
        public void Adopt(Promise inner)
        {
            if (State != PromiseState.Pending || _adopting) return;

            if (inner == null)
            {
                Resolve(null);
                return;
            }

            _adopting = true;
            inner.Then(this, nameof(OnAdopteeFulfilled));
            inner.Catch(this, nameof(OnAdopteeRejected));
        }

        public void Reject(object reason)
        {
            if (State != PromiseState.Pending || _adopting) return;

            State = PromiseState.Rejected;
            Value = reason;

            if (_fulfillCount == 0 && _rejectCount == 0 && _chainCount == 0)
            {
                Debug.LogWarning($"[Promise] Unhandled rejection on '{name}': {reason}");
            }

            if (!HasScheduler()) return;
            DispatchAll();
        }

        public bool IsSettled()
        {
            return State != PromiseState.Pending;
        }

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

        // Internal adoption handlers - this Promise is itself a Then/Catch target
        // of the inner Promise it's waiting on, so it follows the same IncomingPromise
        // read-back convention as everyone else.
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
    }
}