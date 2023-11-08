# Semantico

## Overview

Semantico is a powerful dockerized application designed to provide semantic alerts and notifications.

With Semantico, you can create projects with their respective connection strings to the database (PostgreSQL, MS SQL, or MySQL), and set up personalized queries.

Queries have the option to include placeholders for dynamic data.

Queries can also be used by other users. 

## Getting Started

### Setup - Secrets / Environment Varibles

Please define the connection string using the key:
- ConnectionStrings__SemanticoContext

<b>Semantico currently only supports a PostgreSQL base database.</b>

If you want to use email notification feature, you will also need to define:
- SendGridSettings__Apikey
- SendGridSettings__SenderEmail
- SendGridSettings__SenderName

Expose the port 80 to have API access.

### Example with Docker Compose

You can run Semantico by defining it as such in your compose.yaml
```
services:
  yourApi:
    image: 'yourApi:latest'
    ...
  
  semantico:
    image: 'ghcr.io/moberghr/semantico:latest'
    # env_file: semantico.env # pass setup values through an env file or using secrets
    ports:
      - 8080:80
```

### Semantico Api-Key

To get started with Semantico, you will need a valid Api-Key. The base Semantico Api-Key is provided below:

```
00000000-0000-0000-0000-000000000000
```

Please keep this Api-Key secure and avoid sharing it with unauthorized users.

### Frontend
Currently (v1.0.0), we are still missing FE. You can use all the below mentioned features through Swagger / API calls.

### Projects

1. Log in to Semantico using the Api-Key.
2. Navigate to the "Projects" section.
3. Click on the "Create New Project" button.
4. Fill in the project details: name, database connection string and the database engine type.

Semantico currently supports connections to the following databases:
- PostgreSQL
- MS SQL
- MySQL

### Queries

1. Within a project, navigate to the "Queries" section.
2. Click on the "Create New Query" button.
3. Define your query using the appropriate SQL syntax, and optionally add placeholders for dynamic data.

### Subscriptions

1. Navigate to the "Subscriptions" section within a project.
2. Click on the "Create New Subscription" button.
3. Select the query you want to associate with the subscription.
4. Define a cron expression to regulate the execution frequency.
5. If there are defined query parameters, make sure you define all of the data per your needs.
6. Choose the notification method (email, Teams, or Jira) for results delivery.

## Support and Feedback

Thank you for choosing Semantico! We hope you find it invaluable for managing your alerts and notifications.

If you encounter any issues or have suggestions for improvements, please don't hesitate to open an issue.


