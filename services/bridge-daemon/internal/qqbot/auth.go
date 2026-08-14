package qqbot

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"
)

type TokenProvider struct {
	mu         sync.Mutex
	http       *http.Client
	appID      string
	secret     string
	token      string
	expiresAt  time.Time
	refreshing chan struct{}
	onRefresh  func(time.Time)
	endpoint   string
}

func NewTokenProvider(client *http.Client, appID, secret string, onRefresh func(time.Time)) *TokenProvider {
	return &TokenProvider{http: client, appID: strings.TrimSpace(appID), secret: strings.TrimSpace(secret), onRefresh: onRefresh, endpoint: tokenEndpoint}
}

func (p *TokenProvider) Token(ctx context.Context, force bool) (string, time.Time, error) {
	for {
		p.mu.Lock()
		if !force && p.token != "" && time.Now().Before(p.expiresAt.Add(-tokenRefreshMargin)) {
			token, expiry := p.token, p.expiresAt
			p.mu.Unlock()
			return token, expiry, nil
		}
		if pending := p.refreshing; pending != nil {
			p.mu.Unlock()
			select {
			case <-ctx.Done():
				return "", time.Time{}, newError("qqbot_token_timeout", "QQ access token refresh timed out", ctx.Err())
			case <-pending:
				force = false
				continue
			}
		}
		pending := make(chan struct{})
		p.refreshing = pending
		p.mu.Unlock()

		token, expiry, err := p.fetch(ctx)
		p.mu.Lock()
		if err == nil {
			p.token, p.expiresAt = token, expiry
		}
		p.refreshing = nil
		close(pending)
		p.mu.Unlock()
		if err != nil {
			return "", time.Time{}, err
		}
		if p.onRefresh != nil {
			p.onRefresh(expiry)
		}
		return token, expiry, nil
	}
}

func (p *TokenProvider) Invalidate() {
	p.mu.Lock()
	p.token = ""
	p.expiresAt = time.Time{}
	p.mu.Unlock()
}

func (p *TokenProvider) ExpiresAt() time.Time {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.expiresAt
}

func (p *TokenProvider) fetch(parent context.Context) (string, time.Time, error) {
	if p.appID == "" {
		return "", time.Time{}, newError("qqbot_appid_invalid", "QQ Bot AppID is required", nil)
	}
	if p.secret == "" {
		return "", time.Time{}, newError("qqbot_credentials_missing", "QQ Bot AppSecret is not configured", nil)
	}
	body, _ := json.Marshal(map[string]string{"appId": p.appID, "clientSecret": p.secret})
	ctx, cancel := context.WithTimeout(parent, defaultRequestTimeout)
	defer cancel()
	endpoint := p.endpoint
	if endpoint == "" {
		endpoint = tokenEndpoint
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(body))
	if err != nil {
		return "", time.Time{}, newError("qqbot_protocol_error", "Unable to create QQ token request", err)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "CloudLight-Codex-Bridge/1.0.1")
	response, err := p.http.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return "", time.Time{}, newError("qqbot_token_timeout", "QQ access token request timed out", ctx.Err())
		}
		return "", time.Time{}, newError(networkErrorCategory(err), "Unable to reach the QQ token service", err)
	}
	defer response.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(response.Body, 64*1024))
	if err != nil {
		return "", time.Time{}, newError("qqbot_protocol_error", "Unable to read QQ access token response", err)
	}
	if response.StatusCode == http.StatusTooManyRequests {
		return "", time.Time{}, httpError("qqbot_rate_limited", "QQ token service rate limited the request", response.StatusCode)
	}
	if response.StatusCode == http.StatusUnauthorized || response.StatusCode == http.StatusForbidden || response.StatusCode == http.StatusBadRequest {
		return "", time.Time{}, httpError("qqbot_secret_invalid", "QQ rejected the AppID or AppSecret", response.StatusCode)
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return "", time.Time{}, httpError("qqbot_auth_failed", fmt.Sprintf("QQ token service returned HTTP %d", response.StatusCode), response.StatusCode)
	}
	var result tokenResponse
	if err := json.Unmarshal(raw, &result); err != nil || strings.TrimSpace(result.AccessToken) == "" {
		return "", time.Time{}, newError("qqbot_protocol_error", "QQ token response is incompatible", err)
	}
	result.AccessToken = strings.TrimSpace(result.AccessToken)
	expires, valid := parseExpiresIn(result.ExpiresIn)
	if !valid {
		return "", time.Time{}, newError("qqbot_protocol_error", "QQ token response has an invalid expires_in value", nil)
	}
	return result.AccessToken, time.Now().Add(time.Duration(expires) * time.Second), nil
}

func parseExpiresIn(raw json.RawMessage) (int64, bool) {
	value := strings.Trim(strings.TrimSpace(string(raw)), "\"")
	expires, err := strconv.ParseInt(value, 10, 64)
	if err != nil || expires <= 0 {
		return 0, false
	}
	return expires, true
}
