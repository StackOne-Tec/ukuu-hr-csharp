/**
 * HTTP Digest Authentication for Hikvision ISAPI
 *
 * Hikvision devices use HTTP Digest auth. This module implements
 * a digest auth client that handles the challenge-response flow.
 *
 * Flow:
 *   1. Send request with no auth → 401 + WWW-Authenticate header
 *   2. Parse the WWW-Authenticate digest challenge
 *   3. Compute response hash using MD5
 *   4. Retry with Authorization header
 */

const crypto = require('crypto');

/**
 * MD5 hash helper
 */
function md5(str) {
  return crypto.createHash('md5').update(str).digest('hex');
}

/**
 * Parse WWW-Authenticate header into digest parameters
 */
function parseDigestChallenge(wwwAuth) {
  if (!wwwAuth) return null;

  const match = wwwAuth.match(/^Digest\s+(.+)$/i);
  if (!match) return null;

  const params = {};
  const kvRegex = /(\w+)=(?:"([^"]*)"|([\w/+=]+))/g;
  let kv;
  while ((kv = kvRegex.exec(match[1])) !== null) {
    params[kv[1]] = kv[2] || kv[3];
  }

  return params;
}

/**
 * Compute the digest auth response hash
 */
function computeDigestResponse(params, method, uri, username, password, body) {
  const ha1 = md5(`${username}:${params.realm}:${password}`);
  const ha2 = params.qop === 'auth-int'
    ? md5(`${method}:${uri}:${md5(body || '')}`)
    : md5(`${method}:${uri}`);

  let response;
  if (params.qop === 'auth' || params.qop === 'auth-int') {
    const nc = '00000001';
    const cnonce = crypto.randomBytes(8).toString('hex');
    response = md5(`${ha1}:${params.nonce}:${nc}:${cnonce}:${params.qop}:${ha2}`);
    return { response, nc, cnonce };
  } else {
    response = md5(`${ha1}:${params.nonce}:${ha2}`);
    return { response };
  }
}

/**
 * Build the Authorization header value
 */
function buildAuthHeader(params, username, uri, digestResp) {
  let header = `Digest username="${username}", realm="${params.realm}", nonce="${params.nonce}", uri="${uri}", response="${digestResp.response}"`;

  if (params.qop) {
    header += `, qop=${params.qop}, nc=${digestResp.nc}, cnonce="${digestResp.cnonce}"`;
  }
  if (params.opaque) {
    header += `, opaque="${params.opaque}"`;
  }
  if (params.algorithm) {
    header += `, algorithm=${params.algorithm}`;
  }

  return header;
}

/**
 * Perform an HTTP request with digest authentication
 *
 * @param {string} url - Full URL
 * @param {object} options - fetch options (method, headers, body, etc.)
 * @param {string} username
 * @param {string} password
 * @param {number} timeout - timeout in ms
 * @returns {Promise<Response>}
 */
async function digestFetch(url, options = {}, username, password, timeout = 15000) {
  const { method = 'GET', headers = {}, body = null } = options;

  // Build request headers
  const reqHeaders = {
    ...headers,
    'Accept': headers['Accept'] || 'application/xml, application/json',
  };

  // First attempt (may get 401)
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), timeout);

  let resp;
  try {
    resp = await fetch(url, {
      method,
      headers: reqHeaders,
      body: method !== 'GET' ? body : undefined,
      signal: controller.signal,
    });
  } finally {
    clearTimeout(timeoutId);
  }

  // If not 401, return as-is
  if (resp.status !== 401) return resp;

  // Parse challenge
  const wwwAuth = resp.headers.get('www-authenticate');
  const challenge = parseDigestChallenge(wwwAuth);
  if (!challenge) return resp;

  // Parse URI from URL
  const urlObj = new URL(url);
  const uri = urlObj.pathname + urlObj.search;

  // Compute digest response
  const digestResp = computeDigestResponse(
    challenge, method, uri, username, password,
    typeof body === 'string' ? body : ''
  );

  // Build auth header
  const authHeader = buildAuthHeader(challenge, username, uri, digestResp);

  // Retry with auth
  const controller2 = new AbortController();
  const timeoutId2 = setTimeout(() => controller2.abort(), timeout);

  try {
    return await fetch(url, {
      method,
      headers: {
        ...reqHeaders,
        'Authorization': authHeader,
      },
      body: method !== 'GET' ? body : undefined,
      signal: controller2.signal,
    });
  } finally {
    clearTimeout(timeoutId2);
  }
}

module.exports = {
  digestFetch,
  parseDigestChallenge,
  computeDigestResponse,
};
