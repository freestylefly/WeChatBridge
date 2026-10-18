// Package migrations embeds the SQL files the server applies at boot.
//
// The embed lives here rather than in cmd/wcb because Go's embed patterns
// cannot reach outside their own directory: the runner in cmd/wcb imports
// this package and reads the files off the FS.
package migrations

import "embed"

// FS holds every .sql file in this directory. Sorted by name they apply in
// version order, which is why the names are zero-padded version numbers —
// "0009" sorting before "0010" is the whole trick.
//
//go:embed *.sql
var FS embed.FS
