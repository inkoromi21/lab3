using System.Globalization;
using System.Text;

namespace WpfApp2;

public sealed class InputException(string message) : Exception(message);

public sealed record CompiledFormula(string Expression, Func<double, double> Evaluate);

// Only the operations listed here are accepted; formulas are never executed as C# code.
public static class FormulaInput
{
    private static readonly Dictionary<string, string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan",
        ["sqrt"] = "sqrt", ["ln"] = "ln", ["log"] = "log",
        ["exp"] = "exp", ["abs"] = "abs"
    };

    public static string Normalize(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InputException("Введите формулу f(x).");
        if (source.Length > 200) throw new InputException("Формула слишком длинная (не более 200 символов).");

        var result = new StringBuilder();
        var brackets = new Stack<char>();
        bool expectValue = true;
        int tokens = 0;

        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (++tokens > 128) throw new InputException("Формула слишком сложная (не более 128 элементов).");

            if (IsDigit(c) || c is '.' or ',')
            {
                if (!expectValue) throw new InputException("Между значениями нужен знак операции, например *.");
                int start = i;
                bool digitSeen = false;
                while (i < source.Length && IsDigit(source[i])) { digitSeen = true; i++; }
                if (i < source.Length && source[i] is '.' or ',')
                {
                    i++;
                    while (i < source.Length && IsDigit(source[i])) { digitSeen = true; i++; }
                }
                if (!digitSeen) throw new InputException("Неверная запись числа в формуле.");
                if (i < source.Length && source[i] is 'e' or 'E')
                {
                    int exponentStart = i++;
                    if (i < source.Length && source[i] is '+' or '-') i++;
                    int exponentDigits = i;
                    while (i < source.Length && IsDigit(source[i])) i++;
                    if (i == exponentDigits) i = exponentStart;
                }
                string number = source[start..i].Replace(',', '.');
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                    throw new InputException("В формуле есть недопустимое число.");
                result.Append(value.ToString("G17", CultureInfo.InvariantCulture));
                expectValue = false;
                continue;
            }

            if (IsLetter(c))
            {
                if (!expectValue) throw new InputException("Между значениями нужен знак операции, например *.");
                int start = i;
                while (i < source.Length && IsLetter(source[i])) i++;
                string name = source[start..i];
                if (name.Equals("x", StringComparison.OrdinalIgnoreCase)) result.Append('x');
                else if (name.Equals("pi", StringComparison.OrdinalIgnoreCase)) result.Append("pi");
                else if (name.Equals("e", StringComparison.OrdinalIgnoreCase)) result.Append('e');
                else if (Functions.TryGetValue(name, out string? function))
                {
                    while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
                    if (i >= source.Length || source[i] != '(')
                        throw new InputException($"После {name} нужны скобки, например {name}(x).");
                    if (brackets.Count >= 32) throw new InputException("Слишком много вложенных скобок.");
                    result.Append(function).Append('(');
                    brackets.Push(')');
                    i++;
                    expectValue = true;
                    continue;
                }
                else throw new InputException($"«{name}» не поддерживается.");
                expectValue = false;
                continue;
            }

            i++;
            if (c == '(')
            {
                if (!expectValue) throw new InputException("Перед скобкой нужен знак операции, например *.");
                if (brackets.Count >= 32) throw new InputException("Слишком много вложенных скобок.");
                result.Append('(');
                brackets.Push(')');
            }
            else if (c == ')')
            {
                if (brackets.Count == 0) throw new InputException($"Лишняя закрывающая скобка в позиции {i}.");
                if (expectValue) throw new InputException($"Перед закрывающей скобкой в позиции {i} нет значения.");
                result.Append(brackets.Pop());
                expectValue = false;
            }
            else if (c is '+' or '-')
            {
                result.Append(c);
                expectValue = true;
            }
            else if (c is '*' or '/' or '^')
            {
                if (expectValue) throw new InputException($"Перед «{c}» должно быть значение.");
                result.Append(c);
                expectValue = true;
            }
            else throw new InputException($"Символ «{c}» в формуле не поддерживается.");
        }

        if (brackets.Count != 0) throw new InputException("В формуле не закрыта скобка.");
        if (expectValue) throw new InputException("Формула обрывается после знака операции.");
        return result.ToString();
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static CompiledFormula Compile(string source)
    {
        string expression = Normalize(source);
        return new CompiledFormula(expression, new Parser(expression).Parse());
    }

    private sealed class Parser(string source)
    {
        private int _position;

        public Func<double, double> Parse()
        {
            var formula = AddSubtract();
            SkipSpaces();
            if (_position != source.Length) throw new InputException("Проверьте знаки операций в формуле.");
            return formula;
        }

        private Func<double, double> AddSubtract()
        {
            var left = MultiplyDivide();
            while (true)
            {
                if (Take('+'))
                {
                    var previous = left;
                    var right = MultiplyDivide();
                    left = x => previous(x) + right(x);
                }
                else if (Take('-'))
                {
                    var previous = left;
                    var right = MultiplyDivide();
                    left = x => previous(x) - right(x);
                }
                else return left;
            }
        }

        private Func<double, double> MultiplyDivide()
        {
            var left = Unary();
            while (true)
            {
                if (Take('*'))
                {
                    var previous = left;
                    var right = Unary();
                    left = x => previous(x) * right(x);
                }
                else if (Take('/'))
                {
                    var previous = left;
                    var right = Unary();
                    left = x => previous(x) / right(x);
                }
                else return left;
            }
        }

        private Func<double, double> Unary()
        {
            if (Take('+')) return Unary();
            if (Take('-'))
            {
                var operand = Unary();
                return x => -operand(x);
            }
            return Power();
        }

        private Func<double, double> Power()
        {
            var left = Primary();
            if (!Take('^')) return left;
            var right = Unary();
            return x => Math.Pow(left(x), right(x));
        }

        private Func<double, double> Primary()
        {
            SkipSpaces();
            if (Take('('))
            {
                var inside = AddSubtract();
                Expect(')');
                return inside;
            }

            if (_position < source.Length && (IsDigit(source[_position]) || source[_position] == '.'))
            {
                int start = _position;
                while (_position < source.Length && IsDigit(source[_position])) _position++;
                if (_position < source.Length && source[_position] == '.')
                {
                    _position++;
                    while (_position < source.Length && IsDigit(source[_position])) _position++;
                }
                if (_position < source.Length && source[_position] is 'e' or 'E')
                {
                    _position++;
                    if (_position < source.Length && source[_position] is '+' or '-') _position++;
                    while (_position < source.Length && IsDigit(source[_position])) _position++;
                }
                if (!double.TryParse(source[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    throw new InputException("Неверная запись числа в формуле.");
                return _ => number;
            }

            if (_position < source.Length && IsLetter(source[_position]))
            {
                int start = _position;
                while (_position < source.Length && IsLetter(source[_position])) _position++;
                string name = source[start.._position];
                if (name == "x") return x => x;
                if (name == "pi") return _ => Math.PI;
                if (name == "e") return _ => Math.E;
                Expect('(');
                var argument = AddSubtract();
                Expect(')');
                return name switch
                {
                    "sin" => x => Math.Sin(argument(x)),
                    "cos" => x => Math.Cos(argument(x)),
                    "tan" => x => Math.Tan(argument(x)),
                    "sqrt" => x => Math.Sqrt(argument(x)),
                    "ln" => x => Math.Log(argument(x)),
                    "log" => x => Math.Log10(argument(x)),
                    "exp" => x => Math.Exp(argument(x)),
                    "abs" => x => Math.Abs(argument(x)),
                    _ => throw new InputException($"Неизвестная функция «{name}».")
                };
            }
            throw new InputException("Проверьте формулу и скобки.");
        }

        private bool Take(char value)
        {
            SkipSpaces();
            if (_position >= source.Length || source[_position] != value) return false;
            _position++;
            return true;
        }

        private void Expect(char value)
        {
            if (!Take(value)) throw new InputException("Проверьте формулу и скобки.");
        }

        private void SkipSpaces()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++;
        }
    }
}
