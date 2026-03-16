# Insights Implementation - Summary of Changes

## Overview
Restructured insights to use daily summaries with top 20 high-confidence events, plus daypart (morning/afternoon/night) summaries for today. Added real-time progress tracking and streaming UI updates.

## Key Changes

### 1. Windowing Strategy (`MainWindow.xaml.cs`)
- **Time ranges (24h, 2d, week)**: Now load DAILY summaries instead of hourly
- **Dayparts (morning, afternoon, night)**: Generate single summary for today's daypart only
- Reduced number of LLM calls from potentially hundreds to just daily summaries + optional daypart

### 2. Top 20 High-Confidence Events (`MainWindow.xaml.cs`, `InsightsService.cs`)
- Added `FilterTopEvents()` method that filters summaries to top 20 highest confidence events
- Updated `ParseSummary()` in InsightsService.cs to accept `maxNotableEvents` parameter (default: 20)
- All summary loading now applies this filtering

### 3. Real-Time Progress Tracking (`MainWindow.xaml.cs`)
- Added `_insightsProgressStatus` and `InsightsProgressMessage` properties
- Progress callback passed to `GenerateInsightsOnDemandAsync()` shows current phase, completed/total batches
- UI updates as each summary is loaded/generated via streaming adds to ObservableCollection

### 4. Daily Summary Generation (`InsightsService.cs`)
- Added `FinalizeDailySummaryAsync()` method for generating daily summaries
- Added `BuildDailyPrompt()` with optimized prompt for daily summarization (up to 500 calls)
- Modified `GenerateInsightsOnDemandAsync()` to:
  - Accept optional progress callback parameter
  - Generate one summary per day instead of hourly windows
  - Report progress as each day is processed

### 5. Daypart Summary Generation (`MainWindow.xaml.cs`)
- Added `GenerateDaypartSummaryAsync()` method for morning/afternoon/night summaries
- Generates single summary covering the specified time window (e.g., 4am-12pm for morning)
- Streams result to UI immediately upon completion

### 6. Mode Independence
- Insights mode now runs independently - switching away doesn't cancel LLM requests
- Uses `LoadOfflineCallsFromHistory()` directly when in insights mode, no need for offline call manager

## Files Modified
1. **MainWindow.xaml.cs**
   - Added `_insightsProgressStatus` property
   - Refactored `LoadInsightsSummariesAsync()` with progress tracking
   - Added `GenerateDaypartSummaryAsync()` method
   - Added `FilterTopEvents()` helper method
   - Updated loading logic for daily vs daypart summaries

2. **InsightsService.cs**
   - Modified `GenerateInsightsOnDemandAsync()` to accept progress callback
   - Added `FinalizeDailySummaryAsync()` method
   - Added `BuildDailyPrompt()` method
   - Changed `ExtractMessageContent()` and `BuildEndpoint()` from private to internal

## Performance Improvements
- **Before**: Potentially 96+ LLM calls for "24h" filter (one per 15-min window)
- **After**: Max 24 LLM calls for "24h" filter (one per day), plus optional single daypart call
- Reduced API costs and generation time significantly

## User Experience
- Visual progress indicator shows current phase and completion percentage
- Summaries appear in UI as they're loaded/generated (streaming)
- Status text provides clear feedback on what's happening
- Top 20 highest-confidence events highlighted for quick review
