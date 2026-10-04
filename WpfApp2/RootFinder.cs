using System.Globalization;

namespace WpfApp2;

public sealed record SearchInput(double A, double B, double Epsilon, int Digits, CompiledFormula Formula);
public sealed record RootResult(double X, double Y, double Left, double Right, int Iterations, string Note);

public sealed class RootFinder
{
    private const int MaxIterations = 80;
    private const int DiagnosticIterations = 12;
    private sealed record BracketCheck(RootResult? Root, double Location, bool Undefined);

    public RootResult Find(SearchInput input, double?[] values, CancellationToken cancellationToken)
    {
        int steps = values.Length - 1;
        if (steps < 1) throw new InputException("Недостаточно точек для поиска.");
        if (values.All(value => value is null))
            throw new InputException("Функция не определена в проверенных точках.");

        var exact = new List<int>();
        var brackets = new List<int>();
        for (int i = 0; i <= steps; i++)
        {
            if (values[i] == 0) exact.Add(i);
            if (i < steps && values[i] is double left && values[i + 1] is double right &&
                left != 0 && right != 0 && Math.Sign(left) != Math.Sign(right))
                brackets.Add(i);
        }

        if (exact.Count == values.Length)
            throw new InputException("Функция равна нулю во всех проверенных точках.");
        for (int i = 1; i < exact.Count; i++)
            if (exact[i] == exact[i - 1] + 1)
                throw new InputException($"Несколько соседних нулей около x≈{Format(Point(input, exact[i], steps))}. Сузьте интервал.");

        var roots = new List<RootResult>();
        var undefined = new List<double>();
        var signJumps = new List<double>();
        for (int i = 0; i <= steps; i++)
            if (values[i] is null) undefined.Add(Point(input, i, steps));
        foreach (int index in exact)
        {
            double x = Point(input, index, steps);
            roots.Add(new RootResult(x, 0, x, x, 0, "Корень найден в проверенной точке."));
        }

        foreach (int index in brackets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BracketCheck candidate = Refine(input, Point(input, index, steps), Point(input, index + 1, steps),
                                            values[index]!.Value, values[index + 1]!.Value, cancellationToken);
            if (candidate.Root is not null) roots.Add(candidate.Root);
            else if (candidate.Undefined) undefined.Add(candidate.Location);
            else signJumps.Add(candidate.Location);
        }

        if (roots.Count > 1)
            throw new InputException($"{roots.Count} {RootWord(roots.Count)}: " +
                string.Join("; ", roots.Take(3).Select(root => $"x≈{Format(root.X)}")) +
                (roots.Count > 3 ? $" и ещё {roots.Count - 3}" : "") + ". Сузьте интервал.");
        if (roots.Count == 0)
        {
            if (undefined.Count > 0)
                throw new InputException(undefined.Count == 1
                    ? $"Разрыв: f(x) не определена при x≈{Format(undefined[0])}."
                    : $"Функция не определена в {undefined.Count} точках, например x≈{Format(undefined[0])}.");
            if (signJumps.Count > 0)
                throw new InputException($"Возможный разрыв при x≈{Format(signJumps[0])}: знак меняется без нуля.");
            if (values.All(value => value > 0))
                throw new InputException("f(x) > 0 во всех проверенных точках. Смены знака нет.");
            if (values.All(value => value < 0))
                throw new InputException("f(x) < 0 во всех проверенных точках. Смены знака нет.");
            throw new InputException("Смена знака и точный ноль не найдены.");
        }
        return roots[0];
    }

    private static BracketCheck Refine(SearchInput input, double left, double right,
                                       double fLeft, double fRight, CancellationToken cancellationToken)
    {
        double initialScale = Math.Max(Math.Abs(fLeft), Math.Abs(fRight));
        int iterations = 0;
        while ((right - left > input.Epsilon || iterations < DiagnosticIterations) && iterations < MaxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double middle = left + (right - left) / 2;
            if (middle <= left || middle >= right) break;
            double fMiddle = input.Formula.Evaluate(middle);
            if (!double.IsFinite(fMiddle)) return new BracketCheck(null, middle, true);
            iterations++;
            if (fMiddle == 0)
                return new BracketCheck(new RootResult(middle, 0, middle, middle, iterations,
                    "Корень найден в середине интервала."), middle, false);
            if (Math.Sign(fMiddle) == Math.Sign(fLeft)) { left = middle; fLeft = fMiddle; }
            else { right = middle; fRight = fMiddle; }
        }

        double root = left + (right - left) / 2;
        double residual = input.Formula.Evaluate(root);
        if (!double.IsFinite(residual)) return new BracketCheck(null, root, true);

        // A sign jump at a pole or a step remains large as the bracket shrinks.
        // A real crossing approaches zero. This is a numerical check, not a proof of continuity.
        double smallest = Math.Min(Math.Abs(residual), Math.Min(Math.Abs(fLeft), Math.Abs(fRight)));
        if (smallest > initialScale / 4) return new BracketCheck(null, root, false);
        if (right - left > input.Epsilon)
            throw new InputException($"Недостаточно точности чисел для {input.Digits} знаков.");

        return new BracketCheck(new RootResult(root, residual, left, right, iterations,
            "Корень вычислен как середина итоговых границ дихотомии."), root, false);
    }

    private static double Point(SearchInput input, int index, int steps) =>
        index == steps ? input.B : input.A + (input.B - input.A) * index / steps;

    private static string Format(double value) => value.ToString("G6", CultureInfo.CurrentCulture);

    private static string RootWord(int count) => count % 100 is >= 11 and <= 14 ? "корней" :
        count % 10 == 1 ? "корень" : count % 10 is >= 2 and <= 4 ? "корня" : "корней";
}
