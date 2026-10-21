# Documentation

Technical documentation for developers and operators of Agent Observatory.

| Goal | Document |
|---|---|
| Start the application and sign in | [Local development](local-development.md) |
| Set service endpoints, identity and scheduling | [Configuration reference](configuration.md) |
| Call the execution API and consume live events | [API reference](api.md) |
| Understand feature boundaries and persistence | [Architecture explanation](architecture.md) |
| Run Windows, WSL, browser and Cosmos emulator checks | [Testing how-to](testing.md) |
| Provision resources and deploy the container | [Azure deployment](../azure/README.md) |
| Find local script parameters | [Scripts reference](../scripts/README.md) |
| Build/run the Linux image | [Containers](../containers/README.md) |

Agent behavior is deliberately **not implemented**. The application tests orchestration, persistence, identity gates and visibility, not the correctness of a future agent's decisions.
