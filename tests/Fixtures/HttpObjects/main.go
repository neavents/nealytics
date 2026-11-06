package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"log"
	"net/http"
	"os"
	"time"
)

func main() {
	mux := http.NewServeMux()
	mux.HandleFunc("/ready", func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusOK) })
	mux.HandleFunc("/object", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer "+os.Getenv("SOURCE_TOKEN") || r.URL.Query().Get("selection") != "private-query" {
			http.Error(w, "private source access error", http.StatusUnauthorized)
			return
		}
		data, err := os.ReadFile("/data/object.ndjson")
		if err != nil || len(data) > 1048576 {
			http.Error(w, "private source data error", http.StatusServiceUnavailable)
			return
		}
		checksum := sha256.Sum256(data)
		w.Header().Set("ETag", "\""+hex.EncodeToString(checksum[:])+"\"")
		w.Header().Set("Content-Type", "application/x-ndjson")
		http.ServeContent(w, r, "object.ndjson", time.Time{}, bytes.NewReader(data))
	})
	mux.HandleFunc("/redirect", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/object", http.StatusTemporaryRedirect)
	})
	server := &http.Server{Addr: ":9000", Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	log.Fatal(server.ListenAndServe())
}
