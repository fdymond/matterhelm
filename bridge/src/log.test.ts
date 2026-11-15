import { describe, expect, it } from "vitest";

import { makeLogger } from "./log.js";

describe("makeLogger", () => {
  it("serializes endpoint construction as pino info level 30", () => {
    const lines: string[] = [];
    const logger = makeLogger("info", {
      write: (line) => {
        lines.push(line);
      },
    });

    logger.info(
      {
        evt: "matter.endpoint",
        id: "speaker",
        kind: "speaker",
        endpointName: "HTPC Speaker",
      },
      "endpoint constructed",
    );

    expect(lines).toHaveLength(1);
    expect(JSON.parse(lines[0] ?? "null")).toMatchObject({
      level: 30,
      evt: "matter.endpoint",
      endpointName: "HTPC Speaker",
      msg: "endpoint constructed",
    });
  });
});
