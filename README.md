# Cesario

**Promise-style async for UdonSharp (VRChat).**

Named after Cesario — a character in *Umamusume: Pretty Derby*, and a real-world racehorse.

If you've used Promises in JavaScript, TypeScript, or a similar language, Cesario should
feel familiar: create a promise, resolve or reject it when your async work finishes,
subscribe with `Then`/`Catch`, chain follow-up steps, and combine multiple promises with
`All`/`Race`/`AllSettled`/`Any`. It's adapted for UdonSharp's constraints (no closures, no
`async`/`await`, no custom interfaces), but the mental model — pending → fulfilled or
rejected, once, and callbacks that run off of that — is the same one you already know.

---

## Why

Sequential async logic in Udon usually turns into a pile of boolean flags and step-counters
threaded through `Update()` or chained `SendCustomEventDelayedFrames` calls, because there's
no `async`/`await` to fall back on. Cesario gives you a state machine and a chaining API
instead, so "do this, then that, then handle failure" reads as a short pipeline rather than
a hand-rolled state machine you have to re-derive every time.

---

## Installation

1. Copy the `Cesario` folder into your UdonSharp project (`Assets/Code/Cesario`).
2. Make sure **UdonSharp** and the **VRChat SDK** are already set up in your project.
3. Add a `PromiseSystem` GameObject to your scene with a `PromiseScheduler` and a
   `PromiseFactory` component on it (see [Scene Setup](#scene-setup)).

---

## Quick Start

### Scene Setup

```
GameObject "PromiseSystem"
├── PromiseScheduler (component)
└── PromiseFactory (component)
       PromisePrefab     → Assets/Code/Cesario/Promise.prefab
       CombinatorPrefab  → Assets/Code/Cesario/PromiseCombinator.prefab
       Scheduler         → (the PromiseScheduler above)
```

Both `Promise.prefab` and `PromiseCombinator.prefab` just need the matching component on
them, with Synchronization Method set to **None** — Cesario is entirely client-local,
nothing in it is networked.

`PromiseFactory` pools both promises and combinators rather than instantiating/destroying
them per call — `InitialPoolSize`/`MaxPoolSize` and `InitialCombinatorPoolSize`/
`MaxCombinatorPoolSize` in the inspector control how many are pre-warmed and how large each
pool is allowed to grow before excess instances are destroyed instead of recycled.

### Creating and resolving a promise

```csharp
public class Example : UdonSharpBehaviour
{
    public PromiseFactory Factory;
    [System.NonSerialized] public Promise IncomingPromise;

    private Promise _pending;

    void Start()
    {
        _pending = Factory.Create();
        _pending.Then(this, nameof(OnSuccess));
        _pending.Catch(this, nameof(OnFailure));

        SendCustomEventDelayedSeconds(nameof(ResolveLater), 1f);
    }

    public void ResolveLater() => _pending.Resolve("Hello, Cesario!");
    public void OnSuccess() => Debug.Log($"Fulfilled with: {IncomingPromise.Value}");
    public void OnFailure() => Debug.LogWarning($"Rejected: {IncomingPromise.Value}");
}
```

Every callback target — anything passed to `Then`, `Catch`, or `Chain` — needs a public
`Promise IncomingPromise` field. Cesario populates it right before firing your callback, so
by the time your method runs, `IncomingPromise.Value` has whatever was resolved or rejected.
This stands in for passing the value as a normal argument, since UdonSharp can't dispatch
callbacks with typed parameters the way `.then(value => ...)` does in JS.

When you're done with a promise you created and consumed privately (not one you handed to
other code via `Chain`), return it with `Factory.Return(promise)` so it goes back to the
pool instead of sitting around as a live GameObject.

### Chaining

```csharp
downloader.StartDownload(url)
    .Chain(parseStep, nameof(ParseStep.OnParsed), nameof(ParseStep.OnParseError))
    .Then(consumer, nameof(Consumer.OnPipelineComplete));
```

`Chain` is what actually returns a new promise you can keep chaining off of — a plain
`Then`/`Catch` call doesn't (more on that below). A chain handler reports its result by
setting a field before its method returns:

```csharp
public class ParseStep : UdonSharpBehaviour
{
    [System.NonSerialized] public Promise IncomingPromise;
    [System.NonSerialized] public object ChainResult;

    public void OnParsed()
    {
        string raw = (string)IncomingPromise.Value;
        ChainResult = ParseIt(raw); // becomes the next promise's value
    }

    public void OnParseError()
    {
        ChainResult = DefaultValue; // recovers - the chain continues fulfilled
    }
}
```

If a step needs to hand off to *another* promise instead of a plain value (say, `OnParsed`
itself kicks off more async work), set `ChainResultAdoptee` to that promise instead of
`ChainResult` — the next step in the chain will wait on it.

If a chain handler throws instead of returning normally, the promise it was meant to settle
rejects with the exception rather than hanging pending forever — you don't need to wrap your
own handler in a try/catch just to avoid a stuck chain, though catching and calling
`ChainResultAdoptee`/rejecting yourself still gives you more control over the reason.

### Combining promises

```csharp
var all = Factory.All(new Promise[] { promiseA, promiseB, promiseC });
all.Then(this, nameof(OnAllDone));

public void OnAllDone()
{
    object[] values = (object[])IncomingPromise.Value; // one entry per input, same order
}
```

| Method | Fulfills when | Rejects when |
|---|---|---|
| `Factory.All(promises)` | every input fulfills — value is `object[]` of results | any input rejects — reason is that input's reason |
| `Factory.Race(promises)` | the first input to settle fulfills | the first input to settle rejects |
| `Factory.AllSettled(promises)` | every input has settled, always — never rejects | never |
| `Factory.Any(promises)` | the first input to fulfill | every input rejects — reason is an `object[]` of all rejection reasons |

`AllSettled`'s result is an `object[]` where each entry is itself a 2-element `object[]`:
index `0` is a `bool` (`true` if that input fulfilled, `false` if it rejected), index `1` is
the value or reason accordingly.

```csharp
object[][] outcomes = (object[][])IncomingPromise.Value;
foreach (var entry in outcomes)
{
    bool fulfilled = (bool)entry[0];
    object valueOrReason = entry[1];
}
```

`Race` and `Any` reject immediately on an empty or null input array (`Race` with a
descriptive reason, `Any` with an empty reasons array) rather than the spec's "never
settles" — a combinator that can't ever complete is a worse failure mode in this environment
than a clear immediate rejection.

---

## Core Types

| Type | What it does |
|---|---|
| `Promise` | The promise itself — state, value, and subscriptions. |
| `PromiseFactory` | Creates and pools promises and combinators. Always create promises/combinators through this rather than instantiating one directly. |
| `PromiseScheduler` | Makes sure callbacks never fire in the same frame that settled the promise, and converts a throwing chain handler into a rejection. One per scene, lives alongside the factory. |
| `PromiseCombinator` | Backs `All`/`Race`/`AllSettled`/`Any` — you shouldn't need to touch this directly. |
| `PromiseState` | `Pending`, `Fulfilled`, or `Rejected`. |
| `CombinatorMode` | Which of the four combinator behaviors a `PromiseCombinator` instance is running. |

## API at a Glance

| Method | What it's for |
|---|---|
| `Factory.Create()` | Make a new, pending promise. |
| `Factory.Return(promise)` | Return a finished, unsubscribed promise to the pool. |
| `Factory.Fulfilled(value)` / `Factory.Rejected(reason)` | Make an already-settled promise. |
| `Factory.All(promises)` / `Race(promises)` / `AllSettled(promises)` / `Any(promises)` | Combine multiple promises into one — see [Combining promises](#combining-promises). |
| `promise.Resolve(value)` | Fulfill it. |
| `promise.Reject(reason)` | Reject it. |
| `promise.Then(target, eventName)` | Run something when it fulfills. |
| `promise.Catch(target, eventName)` | Run something when it rejects. |
| `promise.Chain(target, onFulfilledEvent, onRejectedEvent)` | Run a step and get a new promise for the next one. |
| `promise.Adopt(otherPromise)` | Settle this promise by waiting on another one. Rejects immediately if you pass the promise itself. |
| `promise.IsSettled()` / `IsPending()` / `IsFulfilled()` / `IsRejected()` | Check current state. |

---

## How faithful is this to real Promises?

Close in spirit, not identical in every detail — mainly because UdonSharp doesn't have
closures, `async`/`await`, or (currently) custom interfaces, so a few things had to be
adapted or simplified. Compared against the actual [Promises/A+](https://promisesaplus.com/)
specification:

- **`Then`/`Catch` don't return a new promise** — only `Chain` does. In JavaScript, every
  `.then()` call is chainable; here, plain subscriptions are fire-and-forget, and `Chain` is
  the dedicated tool for building a pipeline. This is a deliberate choice, not a gap: making
  every `Then`/`Catch` call also allocate a promise nobody uses would add real, avoidable
  cost to the most common case (subscribe and don't need a continuation), especially with
  many subscribers on one promise.
- **Callbacks are deferred by one frame, not a JS-style microtask.** They're guaranteed to
  never run synchronously with whatever settled the promise, which is the requirement that
  actually matters — but a long chain resolves over several frames rather than instantly
  within one tick.
- **No automatic detection of "is this value actually a promise."** If a step needs to wait
  on another promise instead of settling with a plain value, that's explicit — either call
  `Adopt()` directly, or set `ChainResultAdoptee` in a chain handler. Nothing tries to guess,
  since runtime type-checking against custom types isn't reliable in the UdonSharp compiler
  version this was built against.
- **A promise adopted with itself rejects immediately**, matching the spec's intent (self-
  resolution is treated as an error, not left to hang forever waiting on itself).
- **A throwing chain handler becomes a rejection**, matching the spec — the promise it would
  have settled rejects with the exception instead of staying pending forever. This only
  applies to `Chain`; plain `Then`/`Catch` have no downstream promise for a thrown exception
  to convert into.
- **Values are untyped (`object`).** UdonSharp doesn't support generics, so there's no
  `Promise<T>` — the caller and whoever reads `.Value` just need to agree on the type.

None of this is meant to be a drop-in mental model swap from JS Promises — it's close
enough that the patterns transfer, with the differences above being the places where "just
like JS" would lead you wrong.

---

## Known Gaps

- **Concurrent producers.** If you need multiple in-flight operations from the same
  behaviour (rather than one at a time), you'll need to track multiple pending promises
  yourself — Cesario doesn't provide a pattern for this out of the box.
- **`Return()` timing is manual, not reference-counted.** Only call `Factory.Return()` on a
  promise you're certain has no other live subscribers — returning one that another part of
  your code is still waiting on via `Then`/`Catch` will hand out a reset promise mid-flight
  to whoever else was watching it.
- **No dedicated job-queue construct.** Promises/A+ has no queue primitive of its own, since
  chaining already provides ordering — if dynamically appending jobs to an already-running
  sequence is ever needed, a small helper holding a "tail" `Promise` and appending via
  `Chain()` on each call covers it without a second field-name contract.

---

## License

MIT License — Copyright 2026 Ayase Minori, Umamusume Racing Society Contributors.