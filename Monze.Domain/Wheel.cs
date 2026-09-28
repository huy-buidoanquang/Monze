namespace Monze.Domain;

public static class Wheel
{
    public static int PickIndex(IReadOnlyList<int> weights, int roll)
    {
        if (weights.Count == 0)
        {
            throw new ArgumentException("Wheel needs at least one prize.", nameof(weights));
        }

        var total = 0;
        foreach (var weight in weights)
        {
            if (weight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(weights));
            }

            total += weight;
        }

        var cursor = roll % total;
        for (var i = 0; i < weights.Count; i++)
        {
            cursor -= weights[i];
            if (cursor < 0)
            {
                return i;
            }
        }

        return weights.Count - 1;
    }
}
