namespace LowLevelDesign.AtmMachine;

//=========== enums ===========
public enum TransactionType
{
    Withdraw,
    Deposit,
    BalanceInquiry
}

public enum DenominationType
{
    Hundred,
    FiveHundred,
    Thousand
}

//============ Entities ========================
public class Card
{
    public string cardNumber { get; }
    public DateTime expiryDate { get; }
    public string pin { get; private set; }
    public string cvv { get; }

    public Card(string cardNumber, DateTime expiryDate, string pin, string cvv)
    {
        this.cardNumber = cardNumber;
        this.expiryDate = expiryDate;
        this.pin = pin;
        this.cvv = cvv;
    }

    public void UpdatePin(string pin) => this.pin = pin;

    public bool ValidatePin(string enteredPin) => this.pin == enteredPin;
}

public class Account
{
    public string accountNumber { get; }
    public DateTime createdOn { get; }
    public List<Card> cards { get; }
    public string accountHolderName { get; }
    public decimal balance { get; private set; }

    public Account(string accountNumber, DateTime createdOn, string accountHolderName, List<Card> cards, decimal balance = 0)
    {
        this.accountNumber = accountNumber;
        this.createdOn = createdOn;
        this.accountHolderName = accountHolderName;
        this.cards = cards;
        this.balance = balance;
    }

    public bool AddCard(Card card)
    {
        if (cards.Contains(card))
            return false;
        cards.Add(card);
        return true;
    }

    public bool HasSufficientBalance(decimal amount) => balance >= amount;

    public void Debit(decimal amount)
    {
        if (amount > balance)
            throw new InvalidOperationException("Insufficient account balance");
        balance -= amount;
    }

    public void Credit(decimal amount) => balance += amount;
}

// Thrown by the dispenser chain when the machine physically cannot make up the amount.
public class InsufficientCashException : Exception
{
    public InsufficientCashException(string message) : base(message) { }
}

// ================= Repository ==============
public interface IAccountRepository
{
    Account GetAccountByCard(Card card);
    void Save(Account account);
}

public class InMemoryAccountRepository : IAccountRepository
{
    private readonly Dictionary<string, Account> accountsByCardNumber = new();

    public void Register(Card card, Account account)
    {
        accountsByCardNumber[card.cardNumber] = account;
    }

    public Account GetAccountByCard(Card card)
    {
        if (!accountsByCardNumber.TryGetValue(card.cardNumber, out var account))
            throw new InvalidOperationException($"No account found for card {card.cardNumber}");
        return account;
    }

    public void Save(Account account)
    {
        // No-op for in-memory version:
    }
}

public class CashInventory
{
    // amount here = total monetary value held in that denomination (e.g. 20 notes of 1000 => 20000)
    private readonly Dictionary<DenominationType, decimal> availableDenominationAmounts = new();

    public decimal TotalAvailableAmount => availableDenominationAmounts.Values.Sum();

    public void AddDenominationAmount(DenominationType denominationType, decimal amount)
    {
        if (!availableDenominationAmounts.ContainsKey(denominationType))
        {
            availableDenominationAmounts[denominationType] = 0;
        }
        availableDenominationAmounts[denominationType] += amount;
    }

    // Public read accessor, encapsulation kept intact — callers never touch the dictionary directly.
    public decimal GetAvailableAmount(DenominationType denominationType) =>
        availableDenominationAmounts.GetValueOrDefault(denominationType, 0);

    // Called only after a dispense plan has been fully computed and confirmed.
    public void Deduct(DenominationType denominationType, decimal amount)
    {
        if (GetAvailableAmount(denominationType) < amount)
            throw new InvalidOperationException("Cannot deduct more than available");
        availableDenominationAmounts[denominationType] -= amount;
    }
}

// ================= cash dispense through chain of responsibility ==============

public interface ICashDispenser
{
    Dictionary<DenominationType, int> Dispense(decimal amount);
}

// Terminal handler: if anything is left unhandled here, the machine truly can't dispense it.
public class NoCashDispenser : ICashDispenser
{
    public Dictionary<DenominationType, int> Dispense(decimal amount)
    {
        if (amount == 0)
            return new Dictionary<DenominationType, int>();

        throw new InsufficientCashException($"Cannot dispense remaining amount: {amount}");
    }
}

public abstract class BaseDenominationDispenser : ICashDispenser
{
    protected readonly CashInventory cashInventory;
    protected readonly ICashDispenser nextCashDispenser;
    protected abstract DenominationType Denomination { get; }
    protected abstract int Value { get; }

    protected BaseDenominationDispenser(CashInventory cashInventory, ICashDispenser nextCashDispenser)
    {
        this.cashInventory = cashInventory;
        this.nextCashDispenser = nextCashDispenser;
    }

    public Dictionary<DenominationType, int> Dispense(decimal amount)
    {
        int required = (int)(amount / Value);
        int inventoryCount = (int)(cashInventory.GetAvailableAmount(Denomination) / Value);
        int notesToUse = Math.Min(required, inventoryCount);
        decimal remaining = amount - (notesToUse * Value);

        var result = remaining > 0
            ? nextCashDispenser.Dispense(remaining)
            : new Dictionary<DenominationType, int>();

        if (notesToUse > 0)
            result[Denomination] = notesToUse;

        return result;
    }
}

public class Thousand : BaseDenominationDispenser
{
    protected override DenominationType Denomination => DenominationType.Thousand;
    protected override int Value => 1000;
    public Thousand(CashInventory cashInventory, ICashDispenser nextCashDispenser)
        : base(cashInventory, nextCashDispenser) { }
}

public class FiveHundred : BaseDenominationDispenser
{
    protected override DenominationType Denomination => DenominationType.FiveHundred;
    protected override int Value => 500;
    public FiveHundred(CashInventory cashInventory, ICashDispenser nextCashDispenser)
        : base(cashInventory, nextCashDispenser) { }
}

public class Hundred : BaseDenominationDispenser
{
    protected override DenominationType Denomination => DenominationType.Hundred;
    protected override int Value => 100;
    public Hundred(CashInventory cashInventory, ICashDispenser nextCashDispenser)
        : base(cashInventory, nextCashDispenser) { }
}

// ==================== IATMState.cs ====================
public interface IATMState
{
    void InsertCard(Card card);
    void AuthenticateCard(string pin);
    void SelectOption(TransactionType optionType);
    void WithdrawMoney(decimal amount);
    void DepositMoney(decimal amount);
    void DispenseMoney(decimal amount);
    void EjectCard();
}

// ==================== BaseATMState.cs ====================
public abstract class BaseATMState : IATMState
{
    protected readonly ATMMachine atm;

    protected BaseATMState(ATMMachine atm)
    {
        this.atm = atm;
    }

    public virtual void InsertCard(Card card) => throw new InvalidOperationException(
        $"InsertCard not supported in {GetType().Name}");

    public virtual void AuthenticateCard(string pin) => throw new InvalidOperationException(
        $"AuthenticateCard not supported in {GetType().Name}");

    public virtual void SelectOption(TransactionType optionType) => throw new InvalidOperationException(
        $"SelectOption not supported in {GetType().Name}");

    public virtual void WithdrawMoney(decimal amount) => throw new InvalidOperationException(
        $"WithdrawMoney not supported in {GetType().Name}");

    public virtual void DepositMoney(decimal amount) => throw new InvalidOperationException(
        $"DepositMoney not supported in {GetType().Name}");

    public virtual void DispenseMoney(decimal amount) => throw new InvalidOperationException(
        $"DispenseMoney not supported in {GetType().Name}");

    public virtual void EjectCard() => throw new InvalidOperationException(
        $"EjectCard not supported in {GetType().Name}");
}

// =================== Concrete ATM States ============================

public class CardNotInsertedState : BaseATMState
{
    public CardNotInsertedState(ATMMachine atm) : base(atm) { }

    public override void InsertCard(Card card)
    {
        atm.CurrentCard = card;
        atm.PinRetryCount = 0;
        atm.CurrentState = atm.CardInsertedState;
    }
}

public class CardInsertedState : BaseATMState
{
    public CardInsertedState(ATMMachine atm) : base(atm) { }

    public override void AuthenticateCard(string pin)
    {
        if (atm.CurrentCard.ValidatePin(pin))
        {
            atm.PinRetryCount = 0;
            atm.CurrentAccount = atm.AccountRepository.GetAccountByCard(atm.CurrentCard);
            atm.CurrentState = atm.DisplayOptionsState;
            return;
        }

        atm.PinRetryCount++;
        if (atm.PinRetryCount >= ATMMachine.MaxPinRetries)
        {
            // Max retries exceeded: card ejected, session reset.
            EjectCard();
        }
        // else: stay in CardInsertedState, caller re-prompts for PIN.
    }

    public override void EjectCard()
    {
        atm.ResetSession();
        atm.CurrentState = atm.CardNotInsertedState;
    }
}

public class DisplayOptionsState : BaseATMState
{
    public DisplayOptionsState(ATMMachine atm) : base(atm) { }

    public override void SelectOption(TransactionType optionType)
    {
        switch (optionType)
        {
            case TransactionType.Withdraw:
                atm.CurrentState = atm.WithdrawState;
                break;
            case TransactionType.Deposit:
                atm.CurrentState = atm.DepositState;
                break;
            case TransactionType.BalanceInquiry:
                // No dedicated state: no multi-step input or dispensing involved,
                // handled inline, then session ends.
                atm.ShowBalance(atm.CurrentAccount.balance);
                EjectCard();
                break;
        }
    }

    public override void EjectCard()
    {
        atm.ResetSession();
        atm.CurrentState = atm.CardNotInsertedState;
    }
}

public class WithdrawState : BaseATMState
{
    public WithdrawState(ATMMachine atm) : base(atm) { }

    public override void WithdrawMoney(decimal amount)
    {
        if (!atm.CurrentAccount.HasSufficientBalance(amount))
        {
            atm.ShowError("Insufficient account balance");
            atm.CurrentState = atm.DisplayOptionsState;
            return;
        }

        atm.PendingTransactionAmount = amount;
        atm.CurrentState = atm.DispenseState;
        atm.CurrentState.DispenseMoney(amount);
    }

    public override void EjectCard()
    {
        atm.ResetSession();
        atm.CurrentState = atm.CardNotInsertedState;
    }
}

public class DepositState : BaseATMState
{
    public DepositState(ATMMachine atm) : base(atm) { }

    public override void DepositMoney(decimal amount)
    {
        atm.CurrentAccount.Credit(amount);
        atm.AccountRepository.Save(atm.CurrentAccount);
        // TODO: physical cash-in handling (bill acceptor) is out of scope for this session.
        atm.ResetSession();
        atm.CurrentState = atm.CardNotInsertedState;
    }

    public override void EjectCard()
    {
        atm.ResetSession();
        atm.CurrentState = atm.CardNotInsertedState;
    }
}

public class DispenseState : BaseATMState
{
    public DispenseState(ATMMachine atm) : base(atm) { }

    public override void DispenseMoney(decimal amount)
    {
        try
        {
            var plan = atm.CashDispenserChain.Dispense(amount);

            foreach (var (denomination, count) in plan)
            {
                atm.CashInventory.Deduct(denomination, count * DenominationValue(denomination));
            }

            atm.CurrentAccount.Debit(amount);
            atm.AccountRepository.Save(atm.CurrentAccount);
            atm.ShowDispensedNotes(plan);
            atm.ResetSession();
            atm.CurrentState = atm.CardNotInsertedState;
        }
        catch (InsufficientCashException ex)
        {
            atm.ShowError($"Unable to dispense cash: {ex.Message}");
            atm.CurrentState = atm.DisplayOptionsState;
        }
    }

    private static int DenominationValue(DenominationType d) => d switch
    {
        DenominationType.Thousand => 1000,
        DenominationType.FiveHundred => 500,
        DenominationType.Hundred => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };
}

// ==================== ATMMachine.cs (Context) ====================
public class ATMMachine
{
    public IATMState CurrentState { get; set; }

    public IATMState CardNotInsertedState { get; }
    public IATMState CardInsertedState { get; }
    public IATMState DisplayOptionsState { get; }
    public IATMState WithdrawState { get; }
    public IATMState DepositState { get; }
    public IATMState DispenseState { get; }

    public Card CurrentCard { get; set; }
    public Account CurrentAccount { get; set; }
    public int PinRetryCount { get; set; }
    public const int MaxPinRetries = 3;
    public decimal PendingTransactionAmount { get; set; }

    public CashInventory CashInventory { get; }
    public ICashDispenser CashDispenserChain { get; }
    public IAccountRepository AccountRepository { get; }

    public ATMMachine(CashInventory cashInventory, IAccountRepository accountRepository)
    {
        CashInventory = cashInventory;
        AccountRepository = accountRepository;

        // Chain: Thousand -> FiveHundred -> Hundred -> terminal
        CashDispenserChain = new Thousand(cashInventory,
            new FiveHundred(cashInventory,
                new Hundred(cashInventory,
                    new NoCashDispenser())));

        CardNotInsertedState = new CardNotInsertedState(this);
        CardInsertedState = new CardInsertedState(this);
        DisplayOptionsState = new DisplayOptionsState(this);
        WithdrawState = new WithdrawState(this);
        DepositState = new DepositState(this);
        DispenseState = new DispenseState(this);

        CurrentState = CardNotInsertedState;
    }

    public void ResetSession()
    {
        CurrentCard = null;
        CurrentAccount = null;
        PinRetryCount = 0;
        PendingTransactionAmount = 0;
    }

    public void ShowBalance(decimal balance) => Console.WriteLine($"Balance: {balance}");
    public void ShowError(string message) => Console.WriteLine($"Error: {message}");
    public void ShowDispensedNotes(Dictionary<DenominationType, int> plan)
    {
        foreach (var (denomination, count) in plan)
            Console.WriteLine($"{denomination}: {count} notes");
    }

    // Pass-through methods so client code never touches CurrentState directly.
    public void InsertCard(Card card) => CurrentState.InsertCard(card);
    public void AuthenticateCard(string pin) => CurrentState.AuthenticateCard(pin);
    public void SelectOption(TransactionType type) => CurrentState.SelectOption(type);
    public void WithdrawMoney(decimal amount) => CurrentState.WithdrawMoney(amount);
    public void DepositMoney(decimal amount) => CurrentState.DepositMoney(amount);
    public void EjectCard() => CurrentState.EjectCard();
}