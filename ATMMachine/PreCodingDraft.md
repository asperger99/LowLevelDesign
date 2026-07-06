# ATM Machine — Takeaways

## Mistakes caught during the session (say these out loud if asked in a real interview)

1. **Fat interface with throw-by-default is not "using State pattern," it's a switch-case in disguise** — unless it's paired with a base class that absorbs the boilerplate. Initial instinct was to have every concrete state implement all 8 methods explicitly. Correct approach: lean `IATMState`, `BaseATMState` throws by default, concrete states override only what's legal. This preserves the actual benefit of State pattern (each state's real interface reflects what's valid there) without repeating throw-boilerplate everywhere.

2. **Retry counter placement — got this wrong on the first pass.** Initially placed on `DisplayOptionsState`, which is unreachable until *after* PIN succeeds, so it can never track failed PIN attempts. Root cause of the confusion: not distinguishing "which state is active" from "where does mutable session data live." Correct placement: `ATMMachine` (the context), not any state instance.

3. **Why retry count can't live on the state instance itself** — if a new `CardInsertedState` object gets constructed on every failed attempt (or even just conceptually re-entered), a field on that instance resets to zero. States should be stateless and reusable (can even be singletons), any data that must persist across calls belongs on the context.

4. **Math.Max vs Math.Min bug in the denomination handler** — used `Math.Max(required, inventoryCount)` instead of `Math.Min`. Max would claim to have more cash available than physically exists (over-dispense risk). This is the kind of bug that would pass a quick glance because both are "just picking one of two numbers," but only Min is correct for a capacity constraint.

5. **Direct field access breaking encapsulation** — `Thousand` handler read `CashInventory`'s private dictionary directly, which doesn't even compile. Fixed by adding proper public accessors (`GetAvailableAmount`, `Deduct`) so internal storage stays hidden.

6. **Missing terminal handler in Chain of Responsibility** — chain had no base case for "nothing left in the chain but amount still remains." Any CoR implementation needs an explicit terminal link (`NoCashDispenser` here) that throws or returns cleanly, otherwise you get a NullReferenceException instead of a meaningful domain error.

7. **Double-counting bug in `CashInventory.AddDenominationAmount`** — added the amount once via `Add()` when the key was missing, then added it again unconditionally right after. Classic "handle the missing-key case, then forget the addition already happened" bug.

## Design decisions worth defending in an interview

- No `TransactionCompleteState`: a state that does nothing but immediately transition out adds no value.
- No dedicated `BalanceInquiryState`: no multi-step input or dispensing involved, handled inline in `DisplayOptionsState`.
- Business-rule validation (insufficient account balance) checked in `WithdrawState` *before* reaching `DispenseState`, so `DispenseState`'s only responsibility is "given a validated amount, get physical notes out" (single responsibility).
- Repository pattern for account persistence (`IAccountRepository`) — keeps `ATMMachine` and states decoupled from storage details, easy to swap in a real DB-backed implementation later.
- Card ejected (not retained) on max PIN retries — simpler choice for this session, real ATMs often retain the card for security, worth mentioning as an alternative if asked.

## For next session
- Revisit whether `DispenseState.DispenseMoney` should be split into separate "compute dispense plan" and "confirm + deduct" steps.
- Consider whether card validation deserves its own `ICardRepository`, separate from `IAccountRepository`, if cards and accounts are treated as separate aggregates.