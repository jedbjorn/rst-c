// RstifyReassertBudget.cs — cap pre-render corrections when a binding keeps
// re-showing hidden tabs. During a rule, only the UI's Background callback may
// refill this budget, after Input and Render have had a chance to run.
// Stopping the rule resets it so the next rule starts with a full budget.

namespace RST.Core.Profiles;

public sealed class RstifyReassertBudget
{
    private const int Limit = 3;
    private int _remaining = Limit;

    public bool TryTake()
    {
        if (_remaining == 0) return false;
        _remaining--;
        return true;
    }

    public void Refill() => _remaining = Limit;
}
