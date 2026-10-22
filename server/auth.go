package main

import (
	"crypto/subtle"
	"net/http"
	"strings"
)

func (s *Server) auth(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		h := r.Header.Get("Authorization")
		ok := len(h) > 7 && strings.EqualFold(h[:7], "bearer ") &&
			subtle.ConstantTimeCompare([]byte(h[7:]), []byte(s.token)) == 1
		if !ok {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		next.ServeHTTP(w, r)
	})
}
