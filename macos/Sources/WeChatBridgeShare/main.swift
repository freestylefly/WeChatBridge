// Intentionally empty.
//
// An app extension is entered at `NSExtensionMain`, not at `main`: the linker
// flag in Package.swift redirects the Mach-O entry point, and NSExtensionMain
// instantiates `NSExtensionPrincipalClass` from this bundle's Info.plist.
// SwiftPM still requires an executable target to own a top-level entry file, so
// this one exists to satisfy the build system and is never executed.
