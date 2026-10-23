# Collection components

These components inherit WPF semantic controls for keyboard navigation and UI Automation, but render through the scoped `CollectionComponents.xaml` templates. Merge that dictionary into the collection window. It imports `Themes/AppTheme.xaml` and references the main application’s palette with `DynamicResource`; it defines no separate palette or independent system-theme switch.

- `CollectionButton`: primary, secondary, ghost, icon, segment and danger kinds; focus, hover, pressed and disabled states.
- `CollectionTextBox`: placeholder, input focus and editing semantics.
- `CollectionComboBox`: custom popup and options, type-to-select and standard keyboard selection.
- `CollectionCheckBox` / `CollectionExpander`: custom marks and headers with keyboard toggling.
- `CollectionScrollViewer`: custom track and thumb, wheel and dragging.
- `CollectionTargetList` / `CollectionTargetCard`: custom single selection cards with UI Automation selection support.
- `CollectionMenu` / `CollectionMenuItem`: custom contextual action menus. Merge the owner resources into the menu to preserve theme across popup boundaries.
- `CollectionCard`: reusable rounded surfaces.
- `CollectionFolderWindow`: recent folders, path navigation, parent/child directories. It only returns a chosen existing directory; no files are copied before confirmation.
- `CollectionConfirmationWindow`: modal destructive confirmation; initial focus is on cancel and Escape cancels.

`CollectionImportProgress` is transient receipt feedback. It never mutates the collection ledger. The helper renews its lease during copying and publishes completion only after atomic commit. Expired leases are ignored.
