namespace Monze.Domain;

public static class PointsLedger
{
    public static bool TryApply(
        long balance,
        long delta,
        int awardedToday,
        int dailyCap,
        bool duplicateSource,
        out long nextBalance)
    {
        nextBalance = balance;
        if (duplicateSource || delta == 0)
        {
            return false;
        }

        if (delta > 0 && awardedToday >= dailyCap)
        {
            return false;
        }

        nextBalance = balance + delta;
        return true;
    }
}
