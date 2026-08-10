import { main } from "./cli.js";
import { runJobRunner } from "./job-runner.js";

const promise = process.argv[2] === "__job-runner"
  ? runJobRunner(process.argv[3])
  : main();

promise.catch((error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
