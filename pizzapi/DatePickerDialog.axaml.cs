using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using System;

namespace pizzapi;

public partial class DatePickerDialog : Window
{
    public DateTime? SelectedStart { get; private set; }
    public DateTime? SelectedEnd { get; private set; }

    public DatePickerDialog()
        : this(DateTime.Now.AddDays(-7), DateTime.Now)
    {
    }

    public DatePickerDialog(DateTime defaultStart, DateTime defaultEnd)
    {
        InitializeComponent();
        
        var startPicker = this.FindControl<DatePicker>("StartDatePicker");
        var endPicker = this.FindControl<DatePicker>("EndDatePicker");
        
        if (startPicker != null)
            startPicker.SelectedDate = new DateTimeOffset(defaultStart);
            
        if (endPicker != null)
            endPicker.SelectedDate = new DateTimeOffset(defaultEnd);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var startPicker = this.FindControl<DatePicker>("StartDatePicker");
        var endPicker = this.FindControl<DatePicker>("EndDatePicker");
        
        if (startPicker?.SelectedDate.HasValue == true)
            SelectedStart = startPicker.SelectedDate.Value.DateTime;
            
        if (endPicker?.SelectedDate.HasValue == true)
            SelectedEnd = endPicker.SelectedDate.Value.DateTime;
            
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
