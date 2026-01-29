import http from 'http';
import fs from 'fs';
import path from 'path'; // Added for path manipulation
import url from 'url'; // Added for parsing URL query parameters
import child_process from 'child_process';
import { WebSocketServer } from 'ws';
const httpPort = 8997;
const wsPort = 8998;
const configDir = './config'; // Define the configuration directory
/**
 * Validates that a filename resolves to a path within the config directory.
 * Prevents path traversal attacks including encoded characters.
 * @param fileName The filename to validate
 * @returns The resolved file path if valid, null otherwise
 */
function isPathSafe(fileName) {
    if (!fileName)
        return null;
    // Decode URL-encoded characters and check for path traversal
    const decoded = decodeURIComponent(fileName);
    if (decoded.includes('..') || decoded.includes('/') || decoded.includes('\\')) {
        return null;
    }
    // Ensure the resolved path is within configDir
    const resolvedConfigDir = path.resolve(configDir);
    const filePath = path.resolve(configDir, fileName);
    if (!filePath.startsWith(resolvedConfigDir + path.sep)) {
        return null;
    }
    return filePath;
}
let isSyncRunning = false;
let syncProcess = undefined;
const wsServer = new WebSocketServer({
    port: wsPort
});
function configObjectToCommandLineArr(obj) {
    let retval = [];
    let databaseObj = obj['database'];
    let tallyObj = obj['tally'];
    for (const [key, val] of Object.entries(databaseObj)) {
        retval.push('--database-' + key);
        retval.push(String(val)); // Ensure value is a string
    }
    for (const [key, val] of Object.entries(tallyObj)) {
        retval.push('--tally-' + key);
        retval.push(String(val)); // Ensure value is a string
    }
    return retval;
}
function runSyncProcess(configObj) {
    let cmdArgs = configObjectToCommandLineArr(configObj);
    syncProcess = child_process.fork('./dist/index.js', cmdArgs);
    syncProcess.on('message', (msg) => wsServer.clients.forEach((wsClient) => wsClient.send(msg.toString())));
    syncProcess.on('close', () => {
        isSyncRunning = false;
        wsServer.clients.forEach((wsClient) => wsClient.send('~'));
    });
}
function postTallyXML(tallyServer, tallyPort, payload) {
    return new Promise((resolve, reject) => {
        try {
            let req = http.request({
                hostname: tallyServer,
                port: tallyPort,
                path: '',
                method: 'POST',
                headers: {
                    'Content-Length': Buffer.byteLength(payload, 'utf16le'),
                    'Content-Type': 'text/xml;charset=utf-16'
                }
            }, (res) => {
                let data = '';
                res
                    .setEncoding('utf16le')
                    .on('data', (chunk) => {
                    let result = chunk.toString() || '';
                    data += result;
                })
                    .on('end', () => {
                    resolve(data);
                })
                    .on('error', (httpErr) => {
                    reject(httpErr);
                });
            });
            req.on('error', (reqError) => {
                reject(reqError);
            });
            req.write(payload, 'utf16le');
            req.end();
        }
        catch (err) {
            reject(err);
        }
    });
}
;
const httpServer = http.createServer((req, res) => {
    let reqContent = '';
    const parsedUrl = url.parse(req.url || '', true);
    req.on('data', (chunk) => reqContent += chunk);
    req.on('end', async () => {
        let contentResp = '';
        if (parsedUrl.pathname == '/') {
            let fileContent = fs.readFileSync('./gui.html', 'utf8');
            contentResp = fileContent;
            res.statusCode = 200;
            res.setHeader('Content-Type', 'text/html');
            res.end(contentResp);
            return;
        }
        else if (parsedUrl.pathname == '/list-configs') {
            try {
                const files = fs.readdirSync(configDir).filter(file => file.endsWith('.json'));
                contentResp = JSON.stringify(files);
                res.setHeader('Content-Type', 'application/json');
            }
            catch (err) {
                contentResp = '[]';
                res.setHeader('Content-Type', 'application/json');
            }
        }
        else if (parsedUrl.pathname == '/loadconfig') {
            const fileName = parsedUrl.query.file;
            const filePath = isPathSafe(fileName);
            if (!filePath) {
                res.writeHead(400);
                res.end('Invalid filename');
                return;
            }
            try {
                let fileContent = fs.readFileSync(filePath, 'utf8');
                contentResp = fileContent;
                res.setHeader('Content-Type', 'application/json');
            }
            catch (err) {
                res.writeHead(404);
                res.end('Config file not found');
                return;
            }
        }
        else if (parsedUrl.pathname == '/saveconfig') {
            const fileName = parsedUrl.query.file;
            const filePath = isPathSafe(fileName);
            if (!filePath) {
                res.writeHead(400);
                res.end('Invalid filename');
                return;
            }
            try {
                fs.writeFileSync(filePath, reqContent, { encoding: 'utf8' });
                contentResp = `Config saved to ${fileName}`;
                res.setHeader('Content-Type', 'text/plain');
            }
            catch (err) {
                res.writeHead(500);
                res.end('Error saving config file');
                return;
            }
        }
        // Deletes a specific config file based on the 'file' query parameter.
        else if (parsedUrl.pathname == '/delete-config' && req.method === 'POST') {
            const fileName = parsedUrl.query.file;
            const filePath = isPathSafe(fileName);
            if (!filePath) {
                res.writeHead(400);
                res.end('Invalid filename');
                return;
            }
            try {
                fs.unlinkSync(filePath);
                contentResp = `Deleted ${fileName}`;
                res.setHeader('Content-Type', 'text/plain');
            }
            catch (err) {
                console.error(err);
                res.writeHead(500);
                res.end('Error deleting config file');
                return;
            }
        }
        else if (parsedUrl.pathname == '/sync') {
            let objConfig = JSON.parse(reqContent);
            if (isSyncRunning) {
                contentResp = 'Sync is already running';
            }
            else {
                isSyncRunning = true;
                runSyncProcess(objConfig);
                contentResp = 'Sync started';
            }
            res.setHeader('Content-Type', 'text/plain');
        }
        else if (parsedUrl.pathname == '/abort') {
            if (syncProcess) {
                syncProcess.kill();
                contentResp = 'Process killed';
            }
            else {
                contentResp = 'Could not kill process';
            }
            res.setHeader('Content-Type', 'text/plain');
        }
        else if (parsedUrl.pathname == '/list-company') {
            const reqPayload = '<?xml version="1.0" encoding="utf-8"?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>MyReportLedgerTable</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME="MyReportLedgerTable"><FORMS>MyForm</FORMS></REPORT><FORM NAME="MyForm"><PARTS>MyPart01</PARTS><XMLTAG>DATA</XMLTAG></FORM><PART NAME="MyPart01"><LINES>MyLine01</LINES><REPEAT>MyLine01 : MyCollection</REPEAT><SCROLLED>Vertical</SCROLLED></PART><LINE NAME="MyLine01"><FIELDS>Fld</FIELDS></LINE><FIELD NAME="Fld"><SET>$Name</SET><XMLTAG>ROW</XMLTAG></FIELD><COLLECTION NAME="MyCollection"><TYPE>Company</TYPE><FETCH></FETCH></COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>';
            let objConfig = JSON.parse(reqContent);
            let result = '';
            try {
                result = await postTallyXML(objConfig['server'], objConfig['port'], reqPayload);
            }
            catch {
                result = '<DATA></DATA>';
            }
            contentResp = result;
            res.setHeader('Content-Type', 'text/xml');
        }
        else if (parsedUrl.pathname == '/tally-status') {
            let objConfig = JSON.parse(reqContent);
            try {
                let result = await postTallyXML(objConfig['server'], objConfig['port'], '');
                contentResp = result;
            }
            catch {
                contentResp = '';
            }
            res.setHeader('Content-Type', 'text/plain');
        }
        else {
            res.writeHead(404);
            res.end();
            return;
        }
        res.statusCode = 200;
        res.end(contentResp);
    });
});
httpServer.listen(httpPort, 'localhost', () => {
    if (!fs.existsSync(configDir)) {
        console.log(`Creating configuration directory: ${configDir}`);
        fs.mkdirSync(configDir);
    }
    if (fs.existsSync('./config.json')) {
        console.log('Migrating old config.json to /config directory...');
        fs.renameSync('./config.json', path.join(configDir, 'config.json'));
    }
    console.log(`Server started on http://localhost:httpPort}`);
    console.log('Launching utility GUI page on default browser...');
    child_process.exec(`start http://localhost:${httpPort}`);
    setInterval(() => {
        if (wsServer.clients.size == 0 && !isSyncRunning) {
            console.log('No webpage connected. Closing utility...');
            process.exit(0);
        }
    }, 5000);
});
//# sourceMappingURL=server.js.map