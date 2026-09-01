// TLS-terminating reverse proxy for local dev: https://localhost:8443 -> http://localhost:8000.
// Setup: see ../DEVELOPING.md. Expects cert.pem and key.pem next to this script.
// Run: node local-tls-proxy.js
// Not part of the build: no .csproj references this file.
const https = require('https');
const http = require('http');
const net = require('net');
const fs = require('fs');
const path = require('path');

const LISTEN_PORT = 8443;
const TARGET_HOST = '127.0.0.1';
const TARGET_PORT = 8000;

const certPath = path.join(__dirname, 'cert.pem');
const keyPath = path.join(__dirname, 'key.pem');

if (!fs.existsSync(certPath) || !fs.existsSync(keyPath)) {
  console.error(
    `Missing ${path.basename(certPath)} / ${path.basename(keyPath)} next to this script.\n` +
      'See DEVELOPING.md for the one-time certificate setup.'
  );
  process.exit(1);
}

const options = {
  key: fs.readFileSync(keyPath),
  cert: fs.readFileSync(certPath),
};

const server = https.createServer(options, (req, res) => {
  const headers = Object.assign({}, req.headers, {
    host: `${TARGET_HOST}:${TARGET_PORT}`,
    'x-forwarded-proto': 'https',
    'x-forwarded-host': req.headers.host,
  });

  const proxyReq = http.request(
    { host: TARGET_HOST, port: TARGET_PORT, path: req.url, method: req.method, headers },
    (proxyRes) => {
      res.writeHead(proxyRes.statusCode, proxyRes.headers);
      proxyRes.pipe(res, { end: true });
    }
  );
  proxyReq.on('error', (err) => {
    res.writeHead(502);
    res.end(`Bad gateway: ${err.message}`);
  });
  req.pipe(proxyReq, { end: true });
});

server.on('upgrade', (req, socket, head) => {
  const proxySocket = net.connect(TARGET_PORT, TARGET_HOST, () => {
    let headerLines = `${req.method} ${req.url} HTTP/1.1\r\n`;
    for (let i = 0; i < req.rawHeaders.length; i += 2) {
      headerLines += `${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}\r\n`;
    }
    headerLines += '\r\n';
    proxySocket.write(headerLines);
    proxySocket.write(head);
    proxySocket.pipe(socket);
    socket.pipe(proxySocket);
  });
  proxySocket.on('error', () => socket.destroy());
});

server.listen(LISTEN_PORT, () =>
  console.log(`local-tls-proxy: https://localhost:${LISTEN_PORT} -> http://${TARGET_HOST}:${TARGET_PORT}`)
);
