package main

import "os"

func main() {
	// Protocol failures are serialized on stdout so the host can classify them;
	// diagnostics never go to stdout and no logging is emitted by this process.
	_ = run(os.Stdin, os.Stdout)
}
