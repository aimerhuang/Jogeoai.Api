import "reflect-metadata";

import {
  Body,
  Catch,
  Controller,
  Get,
  Headers,
  HttpException,
  Inject,
  Injectable,
  Module,
  Post,
} from "@nestjs/common";
import type { ArgumentsHost, ExceptionFilter, OnModuleDestroy } from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import { FastifyAdapter, type NestFastifyApplication } from "@nestjs/platform-fastify";
import { loadApiConfig } from "@aivc/config";
import {
  collectorFailureSchema,
  eligibilityRequestSchema,
  heartbeatRequestSchema,
  leaseRequestSchema,
  resultSubmissionSchema,
  sessionEventSchema,
} from "@aivc/contracts";
import {
  checkSessionProfileEligibility,
  createDatabase,
  heartbeatLease,
  leaseTasks,
  migrateDatabase,
  recordSessionEvent,
  reportCollectorFailure,
  submitTaskResult,
  type Database,
} from "@aivc/database";
import { DomainError } from "@aivc/domain";
import type { FastifyReply } from "fastify";
import { ZodError } from "zod";

import { assertCollectorRegion, authenticateCollector } from "./collector-auth.ts";

const config = loadApiConfig();

@Injectable()
class DatabaseService implements OnModuleDestroy {
  public readonly client: Database = createDatabase(config.DATABASE_URL);

  public async onModuleDestroy(): Promise<void> {
    await this.client.end();
  }
}

const collectorServiceTokens = {
  cn: config.COLLECTOR_CN_SERVICE_TOKEN,
  overseas: config.COLLECTOR_OVERSEAS_SERVICE_TOKEN,
};

@Controller()
class HealthController {
  @Get("health")
  public health(): { status: "ok" } {
    return { status: "ok" };
  }
}

@Controller("internal/collector")
class CollectorController {
  public constructor(@Inject(DatabaseService) private readonly database: DatabaseService) {}

  @Post("leases")
  public async lease(
    @Headers("authorization") authorization: string | undefined,
    @Body() body: unknown,
  ) {
    const authorizedRegion = authenticateCollector(authorization, collectorServiceTokens);
    const request = leaseRequestSchema.parse(body);
    assertCollectorRegion(authorizedRegion, request.region);
    const tasks = await leaseTasks(this.database.client, request);
    return { tasks };
  }

  // Read-only pre-check before a profile-mode collector loads the platform page (design §12.2).
  @Post("eligibility")
  public async eligibility(
    @Headers("authorization") authorization: string | undefined,
    @Body() body: unknown,
  ) {
    const authorizedRegion = authenticateCollector(authorization, collectorServiceTokens);
    const request = eligibilityRequestSchema.parse(body);
    assertCollectorRegion(authorizedRegion, request.region);
    return checkSessionProfileEligibility(this.database.client, request);
  }

  @Post("heartbeats")
  public async heartbeat(
    @Headers("authorization") authorization: string | undefined,
    @Body() body: unknown,
  ) {
    const authorizedRegion = authenticateCollector(authorization, collectorServiceTokens);
    const request = heartbeatRequestSchema.parse(body);
    const leaseExpiresAt = await heartbeatLease(this.database.client, {
      ...request,
      region: authorizedRegion,
    });
    return { leaseExpiresAt };
  }

  @Post("results")
  public async result(
    @Headers("authorization") authorization: string | undefined,
    @Body() body: unknown,
  ) {
    const authorizedRegion = authenticateCollector(authorization, collectorServiceTokens);
    const submission = resultSubmissionSchema.parse(body);
    assertCollectorRegion(authorizedRegion, submission.region);
    return submitTaskResult(this.database.client, submission);
  }

  @Post("failures")
  public async failure(
    @Headers("authorization") authorization: string | undefined,
    @Body() body: unknown,
  ) {
    const authorizedRegion = authenticateCollector(authorization, collectorServiceTokens);
    const report = collectorFailureSchema.parse(body);
    assertCollectorRegion(authorizedRegion, report.region);
    return reportCollectorFailure(this.database.client, report);
  }

  @Post("session-events")
  public async sessionEvent(
    @Headers("authorization") authorization: string | undefined,
    @Body() body: unknown,
  ) {
    const authorizedRegion = authenticateCollector(authorization, collectorServiceTokens);
    const event = sessionEventSchema.parse(body);
    assertCollectorRegion(authorizedRegion, event.region);
    return recordSessionEvent(this.database.client, event);
  }
}

@Catch()
class ApiExceptionFilter implements ExceptionFilter {
  public catch(exception: unknown, host: ArgumentsHost): void {
    const reply = host.switchToHttp().getResponse<FastifyReply>();
    if (exception instanceof DomainError) {
      void reply.status(exception.statusCode).send({
        error: { code: exception.code, message: exception.message },
      });
      return;
    }
    if (exception instanceof ZodError) {
      void reply.status(400).send({
        error: { code: "VALIDATION_ERROR", message: "Request validation failed" },
      });
      return;
    }
    if (exception instanceof HttpException) {
      void reply.status(exception.getStatus()).send({
        error: { code: "HTTP_ERROR", message: exception.message },
      });
      return;
    }
    console.error(JSON.stringify({ level: "error", message: "Unhandled API error" }));
    void reply.status(500).send({
      error: { code: "INTERNAL_ERROR", message: "Internal server error" },
    });
  }
}

@Module({
  controllers: [HealthController, CollectorController],
  providers: [DatabaseService],
})
class ApiModule {}

const migrationConnection = createDatabase(config.DATABASE_URL, 1);
try {
  await migrateDatabase(migrationConnection);
} finally {
  await migrationConnection.end();
}
const application = await NestFactory.create<NestFastifyApplication>(
  ApiModule,
  new FastifyAdapter({ logger: false }),
  { logger: ["error", "warn", "log"] },
);
application.useGlobalFilters(new ApiExceptionFilter());
application.enableShutdownHooks();
await application.listen(config.API_PORT, config.API_HOST);
console.log(JSON.stringify({ level: "info", service: "api", port: config.API_PORT }));
