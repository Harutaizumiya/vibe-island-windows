# PROJECT CONTEXT

## Architecture
- App startup -> `MockClaudecodeService` -> `StatusViewModel` -> `MainWindow`
- Mock task feed drives view-model state; the window listens to `PropertyChanged` and turns those state changes into layout, palette, clip, and animation updates.
- Layout constants are centralized in `DynamicIsland/UI/IslandLayout.cs`; animation construction is centralized in `DynamicIsland/Utils/AnimationHelper.cs`.

## Key Files
- `DynamicIsland/App.xaml.cs`: composition root. `OnStartup()` creates the service, view model, main window, and tray icon service.
- `DynamicIsland/Services/MockClaudecodeService.cs`: simulated status source. `StartAsync()`, `OnTimerTick()`, `Schedule()`, and `Publish()` define the demo task lifecycle.
- `DynamicIsland/ViewModels/StatusViewModel.cs`: core state logic. `InitializeAsync()`, `ApplyTask()`, `SetExpanded()`, `ExpandFromHover()`, `CollapseHover()`, and `ToggleExpand()` control status text, actions, compact/collapsed state, and expansion behavior.
- `DynamicIsland/MainWindow.xaml`: main island visual tree. Defines `MainSurface`, `ExpandedRegion`, `ActionPanel`, glyph area, and text/action layout.
- `DynamicIsland/MainWindow.xaml.cs`: window behavior and UI orchestration. `OnViewModelPropertyChanged()`, `UpdateExpansionState()`, `ApplyStatusPalette()`, and `ApplyMainSurfaceClip()` are the main rendering hooks.
- `DynamicIsland/Utils/AnimationHelper.cs`: storyboard factory methods such as `CreateExpandStoryboard()`, `CreateStatusTransitionStoryboard()`, and `CreateBounceStoryboard()`.
- `DynamicIsland/Utils/WindowPositionHelper.cs`: top-center screen placement logic for the transparent host window.
- `DynamicIsland/UI/IslandLayout.cs`: centralized adjustable size constants for collapsed/expanded width, height, spacing, and shell radius.

## Data Flow
1. `App.OnStartup()` creates `MockClaudecodeService`, `StatusViewModel`, and `MainWindow`, then calls `StatusViewModel.InitializeAsync()`.
2. `MockClaudecodeService.StartAsync()` publishes a working task and advances stages through `OnTimerTick()`.
3. `MockClaudecodeService.Publish()` raises `TaskUpdated`.
4. `StatusViewModel.OnTaskUpdated()` calls `ApplyTask()` to update status text, actions, badges, `CollapsedWidth`, and `IsExpanded`.
5. `MainWindow.OnViewModelPropertyChanged()` reacts to `IsExpanded`, `CurrentStatus`, `CollapsedWidth`, and `IsBouncing`.
6. `UpdateExpansionState()` and `ApplyStatusPalette()` apply visual changes; WPF storyboards animate the island shell and expanded content.

## Important Constraints
- Expansion depends on `INotifyPropertyChanged`; property names must stay correct or the island will stop reacting.
- Collapsed width is status-dependent and currently comes from `StatusViewModel.GetCollapsedWidth()`.
- The app is currently mock-driven; no external backend, MQTT broker, or persistence layer is wired in.
- The visible island shape is controlled by `MainSurface` plus `ApplyMainSurfaceClip()`, not only by `CornerRadius`.
- Size tuning should be done in `DynamicIsland/UI/IslandLayout.cs` first, not by scattering values across XAML and code-behind.
