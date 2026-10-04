using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WpfApp2;

public partial class MainWindow : Window
{
    private readonly ChartServiceClient _charts = new();
    private readonly RootFinder _finder = new();
    private bool _busy;
    private bool _initializing = true;
    private double? _root;
    private CancellationTokenSource? _operation;
    private (double A, double B, string Expression)? _sampleKey;
    private double?[]? _samples;

    public MainWindow()
    {
        InitializeComponent();
        _initializing = false;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initializing || ResultText is null) return;
        _sampleKey = null;
        _samples = null;
        _root = null;
        ResultText.Text = "";
        PlotImage.Source = null;
        StatusText.Foreground = Brushes.DarkSlateGray;
        StatusText.Text = "Данные изменены. Постройте график или выполните расчёт заново.";
    }

    private async void Plot_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            SetBusy(true);
            var input = ReadInput();
            StatusText.Text = "Строю график…";
            var samples = GetSamples(input);
            await DrawPlotAsync(input, samples);
            StatusText.Foreground = Brushes.DarkSlateGray;
            StatusText.Text = "";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Solve_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            SetBusy(true);
            _root = null;
            ResultText.Text = "";
            PlotImage.Source = null;
            var input = ReadInput();
            StatusText.Text = "Проверяю функцию на интервале…";
            var samples = GetSamples(input);
            string? searchError = null;
            try
            {
                var result = _finder.Find(input, samples, _operation!.Token);
                _root = result.X;
                string format = "F" + input.Digits.ToString(CultureInfo.InvariantCulture);
                ResultText.Text = $"Корень: x ≈ {result.X.ToString(format, CultureInfo.CurrentCulture)}; " +
                                  $"шагов: {result.Iterations}.";
                StatusText.Foreground = Brushes.DarkSlateGray;
                StatusText.Text = "Строю график…";
            }
            catch (InputException error)
            {
                searchError = error.Message;
                StatusText.Foreground = Brushes.Firebrick;
                StatusText.Text = searchError;
            }
            try
            {
                await DrawPlotAsync(input, samples);
                if (searchError is null) StatusText.Text = "";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = searchError is null ? "Построение графика отменено." :
                    searchError + "\nПостроение графика отменено.";
            }
            catch (Exception plotError)
            {
                string plotMessage = "График: " + plotError.Message;
                StatusText.Text = searchError is null ? plotMessage : searchError + "\n" + plotMessage;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private double?[] GetSamples(SearchInput input)
    {
        var key = (input.A, input.B, input.Formula.Expression);
        if (_sampleKey == key && _samples is not null) return _samples;
        var samples = new double?[257];
        for (int i = 0; i < samples.Length; i++)
        {
            double x = i == samples.Length - 1 ? input.B :
                input.A + (input.B - input.A) * i / (samples.Length - 1);
            double value = input.Formula.Evaluate(x);
            samples[i] = double.IsFinite(value) ? value : null;
        }
        _sampleKey = key;
        _samples = samples;
        return samples;
    }

    private async Task DrawPlotAsync(SearchInput input, double?[] samples)
    {
        byte[] png = await _charts.RenderAsync(input.A, input.B, samples, _root, _operation!.Token);
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        PlotImage.Source = bitmap;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        AText.Clear(); BText.Clear(); EText.Clear(); FormulaText.Clear();
        _root = null; ResultText.Text = ""; PlotImage.Source = null;
        StatusText.Foreground = Brushes.DarkSlateGray;
        StatusText.Text = "Поля очищены.";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private SearchInput ReadInput()
    {
        double a = ParseNumber(AText.Text, "a");
        double b = ParseNumber(BText.Text, "b");
        if (a >= b) throw new InputException("Нужно a < b.");
        if (!double.IsFinite(b - a)) throw new InputException("Слишком большой интервал.");
        double sampleStep = (b - a) / 128;
        if (sampleStep == 0 || a + sampleStep <= a || b - sampleStep >= b)
            throw new InputException("Слишком узкий интервал.");
        if (!int.TryParse(EText.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int digits) || digits is < 0 or > 15)
            throw new InputException("Знаков после запятой: целое число от 0 до 15.");
        double epsilon = Math.Pow(10, -digits);
        CompiledFormula formula = FormulaInput.Compile(FormulaText.Text);
        return new SearchInput(a, b, epsilon, digits, formula);
    }

    private static double ParseNumber(string value, string name)
    {
        string normalized = value.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            throw new InputException($"Поле {name}: введите число.");
        return number;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) _operation = new CancellationTokenSource();
        else { _operation?.Dispose(); _operation = null; }
        PlotItem.IsEnabled = SolveItem.IsEnabled = ClearItem.IsEnabled = !busy;
        CancelItem.IsEnabled = busy;
        AText.IsEnabled = BText.IsEnabled = EText.IsEnabled = FormulaText.IsEnabled = !busy;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private void ShowError(Exception error)
    {
        ResultText.Text = "";
        StatusText.Foreground = Brushes.Firebrick;
        StatusText.Text = error is OperationCanceledException ? "Расчёт отменён." :
            error is InputException or ServiceException ? error.Message : "Ошибка расчёта.";
    }
}
