package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

const emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

type object struct {
	bytes    []byte
	tag      string
	metadata int64
}

type fixture struct {
	access   string
	secret   string
	control  string
	mutex    sync.Mutex
	current  int64
	checksum [32]byte
	versions map[int64]*object
}

func mac(key []byte, text string) []byte {
	digest := hmac.New(sha256.New, key)
	digest.Write([]byte(text))
	return digest.Sum(nil)
}

func authenticate(r *http.Request, access, secret string, now time.Time) bool {
	const scheme = "AWS4-HMAC-SHA256 "
	authorization := r.Header.Get("Authorization")
	if !strings.HasPrefix(authorization, scheme) {
		return false
	}
	fields := strings.Split(strings.TrimPrefix(authorization, scheme), ",")
	if len(fields) != 3 {
		return false
	}
	values := map[string]string{}
	for _, field := range fields {
		pair := strings.SplitN(strings.TrimSpace(field), "=", 2)
		if len(pair) != 2 || values[pair[0]] != "" {
			return false
		}
		values[pair[0]] = pair[1]
	}
	credential := strings.Split(values["Credential"], "/")
	if len(credential) != 5 || credential[0] != access || credential[2] != "auto" || credential[3] != "s3" || credential[4] != "aws4_request" {
		return false
	}
	instant := r.Header.Get("X-Amz-Date")
	clock, err := time.Parse("20060102T150405Z", instant)
	if err != nil || credential[1] != clock.Format("20060102") || clock.Before(now.Add(-15*time.Minute)) || clock.After(now.Add(15*time.Minute)) {
		return false
	}
	names := strings.Split(values["SignedHeaders"], ";")
	sorted := append([]string{}, names...)
	sort.Strings(sorted)
	if strings.Join(names, ";") != strings.Join(sorted, ";") {
		return false
	}
	selected := map[string]bool{}
	var headers strings.Builder
	for _, name := range names {
		if name == "" || name != strings.ToLower(name) || selected[name] {
			return false
		}
		selected[name] = true
		var value string
		if name == "host" {
			value = r.Host
		} else {
			value = strings.Join(r.Header.Values(name), ",")
		}
		if value == "" || strings.TrimSpace(value) != value {
			return false
		}
		headers.WriteString(name + ":" + value + "\n")
	}
	for _, name := range []string{"host", "accept-encoding", "x-amz-date", "x-amz-content-sha256"} {
		if !selected[name] {
			return false
		}
	}
	for _, name := range []string{"range", "if-match", "x-goog-if-generation-match", "x-goog-if-metageneration-match"} {
		if r.Header.Get(name) != "" && !selected[name] {
			return false
		}
	}
	if r.Header.Get("X-Amz-Content-Sha256") != emptyHash {
		return false
	}
	canonical := r.Method + "\n" + r.URL.EscapedPath() + "\n" + r.URL.RawQuery + "\n" + headers.String() + "\n" + values["SignedHeaders"] + "\n" + emptyHash
	checksum := sha256.Sum256([]byte(canonical))
	scope := strings.Join(credential[1:], "/")
	message := "AWS4-HMAC-SHA256\n" + instant + "\n" + scope + "\n" + hex.EncodeToString(checksum[:])
	signing := []byte("AWS4" + secret)
	for _, part := range credential[1:] {
		signing = mac(signing, part)
	}
	supplied, err := hex.DecodeString(values["Signature"])
	return err == nil && hmac.Equal(supplied, mac(signing, message))
}

func (f *fixture) refresh() error {
	input, err := os.Open("/data/object.ndjson")
	if err != nil {
		return err
	}
	defer input.Close()
	bytes, err := io.ReadAll(io.LimitReader(input, 1048577))
	if err != nil || len(bytes) > 1048576 {
		return errors.New("fixture object unavailable")
	}
	checksum := sha256.Sum256(bytes)
	if f.current == 0 || checksum != f.checksum {
		if len(f.versions) == 4 {
			return errors.New("fixture history full")
		}
		if f.current == 0 {
			f.current = 17
		} else {
			f.current++
		}
		f.checksum = checksum
		f.versions[f.current] = &object{bytes: bytes, tag: "\"" + hex.EncodeToString(checksum[:]) + "\"", metadata: 3}
	}
	return nil
}

func positive(value string) (int64, bool) {
	number, err := strconv.ParseInt(value, 10, 64)
	return number, err == nil && number > 0 && strconv.FormatInt(number, 10) == value
}

func (f *fixture) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path == "/ready" {
		w.WriteHeader(200)
		return
	}
	f.mutex.Lock()
	defer f.mutex.Unlock()
	if strings.HasPrefix(r.URL.Path, "/control/") {
		if r.Method != "POST" || !hmac.Equal([]byte(r.Header.Get("X-Fixture-Key")), []byte(f.control)) {
			http.Error(w, "fixture access denied", 403)
			return
		}
		generation, ok := positive(r.URL.Query().Get("generation"))
		if !ok {
			http.Error(w, "fixture generation invalid", 400)
			return
		}
		target, exists := f.versions[generation]
		if !exists {
			w.WriteHeader(404)
			return
		}
		switch r.URL.Path {
		case "/control/metadata":
			target.metadata++
		case "/control/delete":
			delete(f.versions, generation)
		default:
			w.WriteHeader(404)
			return
		}
		w.WriteHeader(204)
		return
	}
	if (r.Method != "GET" && r.Method != "HEAD") || r.URL.Path != "/fixture-bucket/events/file.ndjson" {
		http.Error(w, "fixture object unavailable", 404)
		return
	}
	if !authenticate(r, f.access, f.secret, time.Now().UTC()) {
		http.Error(w, "fixture signature invalid", 403)
		return
	}
	if r.Header.Get("Accept-Encoding") != "identity" {
		http.Error(w, "fixture encoding invalid", 400)
		return
	}
	if err := f.refresh(); err != nil {
		http.Error(w, "fixture source unavailable", 503)
		return
	}
	generation := f.current
	if value, exists := r.URL.Query()["generation"]; exists {
		var ok bool
		if len(value) != 1 {
			http.Error(w, "fixture generation invalid", 400)
			return
		}
		generation, ok = positive(value[0])
		if !ok {
			http.Error(w, "fixture generation invalid", 400)
			return
		}
	}
	target, exists := f.versions[generation]
	if !exists {
		w.WriteHeader(404)
		return
	}
	if (r.Header.Get("If-Match") != "" && r.Header.Get("If-Match") != target.tag) ||
		(r.Header.Get("X-Goog-If-Generation-Match") != "" && r.Header.Get("X-Goog-If-Generation-Match") != strconv.FormatInt(generation, 10)) ||
		(r.Header.Get("X-Goog-If-Metageneration-Match") != "" && r.Header.Get("X-Goog-If-Metageneration-Match") != strconv.FormatInt(target.metadata, 10)) {
		w.WriteHeader(412)
		return
	}
	w.Header().Set("ETag", target.tag)
	w.Header().Set("Content-Type", "application/x-ndjson")
	w.Header().Set("X-Goog-Generation", strconv.FormatInt(generation, 10))
	w.Header().Set("X-Goog-Metageneration", strconv.FormatInt(target.metadata, 10))
	w.Header().Set("X-Goog-Stored-Content-Encoding", "identity")
	w.Header().Set("X-Goog-Stored-Content-Length", strconv.Itoa(len(target.bytes)))
	if r.Method == "HEAD" {
		w.Header().Set("Content-Length", strconv.Itoa(len(target.bytes)))
		w.WriteHeader(200)
		return
	}
	bounds := strings.Split(strings.TrimPrefix(r.Header.Get("Range"), "bytes="), "-")
	if !strings.HasPrefix(r.Header.Get("Range"), "bytes=") || len(bounds) != 2 {
		w.WriteHeader(416)
		return
	}
	start, e1 := strconv.Atoi(bounds[0])
	end, e2 := strconv.Atoi(bounds[1])
	if e1 != nil || e2 != nil || start < 0 || end < start || end >= len(target.bytes) {
		w.WriteHeader(416)
		return
	}
	w.Header().Set("Content-Length", strconv.Itoa(end-start+1))
	w.Header().Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", start, end, len(target.bytes)))
	w.WriteHeader(206)
	w.Write(target.bytes[start : end+1])
}

func selfTest() {
	request, _ := http.NewRequest("GET", "https://storage.googleapis.com/example-bucket/events/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%25.ndjson?generation=1486161811706000", nil)
	request.Header.Set("Accept-Encoding", "identity")
	request.Header.Set("If-Match", "\"tag\"")
	request.Header.Set("Range", "bytes=0-9")
	request.Header.Set("X-Amz-Content-Sha256", emptyHash)
	request.Header.Set("X-Amz-Date", "20130524T000000Z")
	request.Header.Set("X-Goog-If-Generation-Match", "1486161811706000")
	request.Header.Set("X-Goog-If-Metageneration-Match", "15")
	request.Header.Set("Authorization", "AWS4-HMAC-SHA256 Credential=GOOGEXAMPLEACCESSID/20130524/auto/s3/aws4_request,SignedHeaders=accept-encoding;host;if-match;range;x-amz-content-sha256;x-amz-date;x-goog-if-generation-match;x-goog-if-metageneration-match,Signature=bf79576b8e0164426c590373d5c22f09597dee7499a2aa66333879eaa9501be2")
	now, _ := time.Parse("20060102T150405Z", "20130524T000000Z")
	if !authenticate(request, "GOOGEXAMPLEACCESSID", "google-fixture-secret+/=", now) {
		log.Fatal("reference signature self-test failed")
	}
	if authenticate(request, "GOOGEXAMPLEACCESSID", "google-fixture-secret+/=", now.Add(time.Hour)) {
		log.Fatal("signature clock self-test failed")
	}
	request.Header.Set("X-Goog-If-Metageneration-Match", "16")
	if authenticate(request, "GOOGEXAMPLEACCESSID", "google-fixture-secret+/=", now) {
		log.Fatal("signature mutation self-test failed")
	}
}

func main() {
	selfTest()
	if len(os.Args) == 2 && os.Args[1] == "--self-test" {
		return
	}
	server := &fixture{access: os.Getenv("SOURCE_ACCESS_KEY"), secret: os.Getenv("SOURCE_SECRET_KEY"), control: os.Getenv("FIXTURE_CONTROL_KEY"), versions: map[int64]*object{}}
	if server.access == "" || server.secret == "" || server.control == "" {
		log.Fatal("fixture credentials required")
	}
	log.Fatal((&http.Server{Addr: ":9000", Handler: server, ReadHeaderTimeout: 5 * time.Second, MaxHeaderBytes: 16384}).ListenAndServe())
}
