namespace Monze.Domain;

public static class AiBudget
{
    public static bool TryConsume(int usedToday, int requested, int dailyCap, out int nextUsed)
    {
        nextUsed = usedToday;
        if (requested <= 0 || usedToday + requested > dailyCap)
        {
            return false;
        }

        nextUsed = usedToday + requested;
        return true;
    }
}
