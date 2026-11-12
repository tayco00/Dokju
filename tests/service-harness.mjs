import { run, startupError } from '../backend/server.mjs';

run(process.env.DOKJU_TEST_SOCKET).catch(startupError);
