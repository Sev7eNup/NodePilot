namespace NodePilot.Ai.Agents;

// Counts model turns without a changed host progress signature, independently of budgets.
internal sealed class AgentProgressWatch
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _unchanged;
    public bool Stopped => _unchanged >= 24;
    public string Check(string signature)
    {
        if (!Stopped) _unchanged = _seen.Add(signature) ? 0 : _unchanged + 1;
        return _unchanged >= 24
            ? "Host progress stop: repeated turns produced no new observation or completed obligation. Tools are disabled. Return retained findings and explicit unresolved questions; do not claim completion."
            : _unchanged >= 12
                ? "Host progress warning: no new observation or completed obligation in repeated turns. Change to a concrete discriminating check or finish with explicit limitations. Re-reading memory and repeating reviews are not progress."
                : "";
    }
}
