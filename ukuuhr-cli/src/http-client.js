/**
 * HTTP client for Hikvision ISAPI communication
 *
 * Provides digest-auth-aware HTTP client with:
 *   - SSL certificate bypass (self-signed device certs)
 *   - Accept headers for XML + JSON
 *   - Timeout support
 *   - Error body reading
 */

const { digestFetch } = require('./digest-auth');

/**
 * Create an ISAPI HTTP client bound to a device
 */
function createIsapiClient(settings, timeout = 15) {
  const scheme = settings.useHttps ? 'https' : 'http';
  const baseUrl = `${scheme}://${settings.deviceIp}:${settings.devicePort}`;
  const timeoutMs = timeout * 1000;

  return {
    baseUrl,
    settings,
    timeoutMs,

    /**
     * GET request with digest auth
     */
    async get(path) {
      const url = `${baseUrl}${path}`;
      return digestFetch(url, {
        method: 'GET',
        headers: {
          'Accept': 'application/xml, application/json',
        },
      }, settings.deviceUsername, settings.devicePassword, timeoutMs);
    },

    /**
     * POST request with digest auth
     */
    async post(path, body, contentType = 'application/xml') {
      const url = `${baseUrl}${path}`;
      return digestFetch(url, {
        method: 'POST',
        headers: {
          'Accept': 'application/xml, application/json',
          'Content-Type': contentType,
        },
        body: body,
      }, settings.deviceUsername, settings.devicePassword, timeoutMs);
    },

    /**
     * Read error response body (truncated)
     */
    async readErrorBody(resp, maxLen = 300) {
      try {
        const text = await resp.text();
        return text.length <= maxLen ? text : text.substring(0, maxLen) + '...';
      } catch {
        return '';
      }
    },

    /**
     * Generate curl command for an endpoint
     */
    generateCurl(path, method = 'GET', postBody = null) {
      let cmd = `curl -v --digest -u '${settings.deviceUsername}:${settings.devicePassword}'`;
      if (method !== 'GET') cmd += ` -X ${method}`;
      cmd += ` -H 'Accept: application/xml, application/json'`;
      if (postBody) {
        const escaped = postBody.replace(/'/g, "'\\''");
        cmd += ` -H 'Content-Type: application/xml'`;
        cmd += ` -d '${escaped}'`;
      }
      cmd += ` '${baseUrl}${path}'`;
      return cmd;
    },
  };
}

module.exports = { createIsapiClient };
