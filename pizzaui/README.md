
# Introduction
<img align="right" src="http://github.com/lilhoser/pizzawave/raw/main/docs/logo-med.png"> `pizzaui` is a multi-threaded .NET user-interface application built on top of the [`pizzalib`](https://github.com/lilhoser/pizzawave/tree/main/pizzalib) library.

<img align="left" src="http://github.com/lilhoser/pizzawave/raw/main/docs/screenshot1.png"> Please be sure to read the [`pizzawave` README page](https://github.com/lilhoser/pizzawave)

# Requirements
* [Requirements as specified in the `pizzawave` README](https://github.com/lilhoser/pizzawave)
* [Requirements as specified in the `pizzalib` README]((https://github.com/lilhoser/pizzawave/tree/main/pizzalib)
* A Windows system running .NET 8 or later

# Configuration

`pizzaui` provides a powerful interface for editing `pizzawave` configuration (that is, both `pizzaui` and `pizzalib` parameters) through the `Edit->Settings` menu. See [`pizzalib` README](https://github.com/lilhoser/pizzawave/pizzalib) for details on these parameters.

`pizzaui` also manages its own parameters that are stored in the same `settings.json` file used by `pizzalib` parameters.  These parameters are:
* `GroupingStrategy` (default=`Category`): How transcribed calls should be grouped in the UI, by talkgroup field: `Off`=0, `AlphaTag`=1, `Tag`=2, `Description`=3, `Category`=4
* `ShowAlertMatchesOnly` (default=`false`): If set to `false`, all transcribed calls are shown in the UI; if set to `true`, only calls that trigger an alert will be shown.

To backup your settings, use `File->Save settings as...`. To load external settings, use `File->Open settings...`.

# Display Interface

## Exporting

All call data and complete transcriptions shown in the current view can be exported to JSON or CSV from the `View->Export..` sub-menus. Alternatively, you can navigate to `Diagnostics->View logs` to open pizzawave's working directory. Inside this director you'll find full [captures](https://github.com/lilhoser/pizzawave#Running) which you can export manually.

## Opening a capture

To open a `pizzawave` capture from a prior live session, navigate to `File->Open capture`. You can find all of `pizzawave`'s past live-session captures in `<user profile>\pizzawave\captures`.

To open an offline capture created by `callstream`'s SFTP feature, navigate to `File->Open offline capture`.

Read more about captures [here](https://github.com/lilhoser/pizzawave#Running)].

## Viewing full call transcription

Hover over the transcription snippet to view a popup window containing the full text of the transcription.

## Sorting and Filtering

To sort, click on a column header. To filter by any column, right-click on that column and select a value from the `Filtering` sub-menu. These values are populated from the current dataset being displayed. To clear filters, navigate to the same sub-menu and select `Clear All Filters`.

The default sort order is call start time, descending.

## Grouping

To change how the calls are displayed in groups, select `View->Group by` and choose a talkgroup sub-field. These sub-fields come from the talkgroup CSV you specified in settings.

The default grouping is by Talkgroup category.

## Show only calls matching an alert

By default, `pizzaui` will immediately display all calls in its primary listview control as calls are sent from trunk-recorder. Any call that matches an alert will be highlighted in orange. To show only calls that matched an alert, select `View->Show alert matches only`.

## Copying data

To copy rows of data from the display listview, highlight the rows and press CTRL+C.

# Alerts

Alerts are driven by rules which are provided by `pizzalib`. Navigate to `Edit->Alerts` to manage your alert rules. <img align="left" src="http://github.com/lilhoser/pizzawave/raw/main/docs/screenshot2.png">

Please see [the `pizzalib` README](https://github.com/lilhoser/pizzawave/pizzalib) for details on how Alerts work.

# Headless mode

`pizzaui` can be run without a UI at all, in case you prefer the command line experience or if you want to setup `pizzaui` as a local service. <img align="right" src="http://github.com/lilhoser/pizzawave/raw/main/docs/screenshot3.png">

To operate `pizzaui` in headless mode, simply execute `pizzaui.exe --headless` from a command prompt window. If you don't want to use the default settings file, you can pass `--settings=<location>`. If you don't see any output, ensure that your `pizzalib` settings have the `TraceLevelApp` parameter set to something chatty, like `Verbose`. You can also verify the application is running in headless mode by looking for `pizzaui` in task manager or seeing a port listener in `netstat -an` output. And, of course, if your settings are correct and you have defined some alerts that are being triggered by active calls, you can browse the application's working folder to see output.

## `pizzaui` as a service

It's simple to setup `pizzaui` to run as a Windows service. See this [Microsoft Learn page](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/new-service?view=powershell-7.4) for details:

```
$params = @{
  Name = "PizzawaveService"
  BinaryPathName = '<path_to_pizzawave>\pizzaui.exe --headless --settings=<path_to_settings_file>'
  DependsOn = "NetLogon"
  DisplayName = "PizzaWave Service"
  StartupType = "Automatic"
  Description = "Because I like pizza and radio waves."
}
New-Service @params
```

# Other

## Tools

Pizzawave comes with some helper tools to make your life easier, found in `Tools` menu:
* `Transcription quality` - this tool allows you to listen to a WAV file and compare whisper's transcription of that file
* `Find talkgroups` - this tool helps you find talkgroup data to import into pizzawave
* `Cleanup` - erase all of those extra log files and WAV files