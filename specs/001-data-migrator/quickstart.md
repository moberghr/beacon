# Data Migration Tool - Quickstart Guide

## Overview
This guide demonstrates how to set up and execute a simple data migration job using the new Data Migration Tool. The scenario migrates customer data from a PostgreSQL database to a MySQL database with data transformation.

## Prerequisites
- Access to Beacon application with Data Migration module
- Source project configured (PostgreSQL with customer data)
- Destination project configured (MySQL target database)
- Appropriate database permissions for both source and destination

## Scenario: Customer Data Migration

### Step 1: Navigate to Data Migration
1. Open the Beacon application
2. Navigate to **Data Migration** from the main menu
3. You should see the migration jobs list (initially empty)

### Step 2: Create a New Migration Job
1. Click **Create Migration Job** button
2. Fill in the basic information:
   - **Name**: `Customer Data Sync`
   - **Description**: `Daily sync of customer data from main PostgreSQL to reporting MySQL`

### Step 3: Configure Source Query
1. Select **Source Project**: `Main Database (PostgreSQL)`
2. Enter the source query:
   ```sql
   SELECT 
       customer_id,
       first_name,
       last_name,
       email,
       registration_date,
       status
   FROM customers 
   WHERE updated_at >= @lastSyncDate OR @lastSyncDate IS NULL
   ORDER BY customer_id
   ```
3. Click **Validate Query** to ensure syntax is correct

### Step 4: Configure Destination
1. Select **Destination Project**: `Reporting Database (MySQL)`
2. Set **Destination Table**: `customer_data`
3. Choose **Migration Mode**: `Insert or Update` (Upsert)

### Step 5: Set Execution Options
1. **Schedule**: `0 2 * * *` (daily at 2 AM)
2. **Max Retries**: `3`
3. **Timeout**: `30` minutes
4. **Validate Before Execution**: ✓ (checked)

### Step 6: Add Data Transformation (Optional)
If you need to transform the data, add transformation script:
```sql
-- Transform status values
UPDATE @result1 
SET status = CASE 
    WHEN status = 'A' THEN 'Active'
    WHEN status = 'I' THEN 'Inactive' 
    ELSE 'Unknown'
END;

-- Add computed column
ALTER TABLE @result1 ADD COLUMN full_name VARCHAR(100);
UPDATE @result1 SET full_name = CONCAT(first_name, ' ', last_name);
```

### Step 7: Save and Validate
1. Click **Validate** to perform comprehensive validation
2. Verify all validations pass:
   - ✓ Query syntax valid
   - ✓ Source connection successful
   - ✓ Destination table accessible
   - ✓ Schedule expression valid
3. Click **Create** to save the migration job

## Testing the Migration

### Step 8: Manual Execution Test
1. From the migration jobs list, find your `Customer Data Sync` job
2. Click the **Execute** button
3. Confirm the execution in the dialog
4. Monitor the progress in real-time

### Step 9: Review Execution Results
1. Navigate to **Migration History** or click on the execution status
2. Review the execution details:
   - **Status**: Completed
   - **Source Rows Read**: 1,247
   - **Destination Rows Written**: 1,247
   - **Rows Skipped**: 0
   - **Rows Failed**: 0
   - **Duration**: 00:00:45
   - **Performance**: 27.7 rows/second

### Step 10: Verify Data in Destination
1. Check the destination database to confirm data was migrated correctly
2. Verify transformations were applied (status values, full_name column)
3. Confirm data integrity and completeness

## Scheduled Execution

### Step 11: Monitor Scheduled Runs
1. Wait for the scheduled execution (2 AM daily) or modify schedule for testing
2. Monitor executions in the **Migration History** page
3. Set up notifications for failed executions (optional)

## Advanced Features Demo

### Using Parameters
For more dynamic migrations, use parameters in your queries:
```sql
SELECT * FROM orders 
WHERE order_date >= @startDate 
  AND order_date <= @endDate
  AND customer_id IN (@customerIds)
```

### Chaining Results with @result Syntax
For complex transformations:
```sql
-- First query: Get base data
SELECT customer_id, order_total FROM orders;

-- Second transformation: Add calculated fields
SELECT 
    customer_id,
    order_total,
    CASE WHEN order_total > 1000 THEN 'Premium' ELSE 'Standard' END as tier
FROM @result1;

-- Final result ready for destination
SELECT * FROM @result2;
```

### Error Handling Example
To test error handling:
1. Temporarily make destination database unavailable
2. Execute the migration job
3. Observe the error handling:
   - Status changes to "Failed"
   - Error message captured
   - Retry mechanism triggered based on configuration

## Monitoring and Maintenance

### Performance Monitoring
- Track execution duration trends
- Monitor row processing rates
- Set up alerts for performance degradation

### Data Quality Checks
- Compare row counts between source and destination
- Validate critical data transformations
- Monitor for data consistency issues

### Troubleshooting Common Issues
1. **Connection Failures**: Check database connectivity and credentials
2. **Query Timeouts**: Increase timeout or optimize query performance
3. **Schema Mismatches**: Verify destination table structure
4. **Schedule Issues**: Validate cron expressions and timezone settings

## Best Practices Demonstrated

### Query Design
- Use incremental updates with date filters
- Include proper ordering for consistent results
- Parameterize dynamic values

### Error Prevention
- Always validate before scheduling
- Test with small data sets first
- Use appropriate migration modes for your use case

### Performance Optimization
- Index source tables for query performance
- Use appropriate batch sizes for large datasets
- Schedule during low-traffic periods

### Maintenance
- Regular monitoring of execution history
- Cleanup old execution records periodically
- Update schedules based on business requirements

## Expected Outcomes

After completing this quickstart:
1. ✅ Successfully created a migration job
2. ✅ Executed manual migration with validation
3. ✅ Configured automated scheduling
4. ✅ Monitored execution history and performance
5. ✅ Understood error handling and retry mechanisms
6. ✅ Applied data transformations using @result syntax

The Data Migration Tool is now ready for production use with your specific data migration requirements.