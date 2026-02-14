# Latency_Prediction_AR
## Overview
This repository contains the code for the machine learning–driven latency prediction and closed-loop control framework for augmented reality (AR) systems using Microsoft HoloLens 2. The project aims to predict near-future frame latency and proactively adapt rendering workload to bound extreme latency events while maintaining real-time performance within a 60 FPS frame budget.
## Introduction
The predictive latency control framework integrates a Temporal Convolutional Network (TCN)–based forecasting model with a conservative feedback-driven adaptation controller. The system is implemented in Unity using OpenXR on Microsoft HoloLens 2 and operates entirely on-device without cloud offloading.

The framework evaluates three execution modes:

Baseline mode (no prediction, no adaptation)

Prediction-only mode (forecasting without actuation)

Closed-loop mode (forecasting integrated with runtime adaptation)

The goal is to improve worst-case latency stability while preserving rendering quality and maintaining strict real-time compliance.

## Experimental Setup

The experimental setup involves deploying the Unity application to Microsoft HoloLens 2 and evaluating runtime performance under dynamic workload conditions.

Workload is generated through:

Continuous rotational motion of holographic objects

Dynamic instantiation and removal of hologram prefabs

Object count variation between 10 and 400 holograms

User walking with in-scene prompts to induce motion variability

Each session consists of:

Baseline run (data collection and reference performance)

Prediction-only run (open-loop forecasting)

Closed-loop runs (feedback-driven adaptation enabled)

Per-frame logging captures:

Measured latency

Predicted latency

Smoothed latency

Controller state

Quality level

Deadline miss events

Primary evaluation metrics include:

Median latency

P95 and P99 latency

Worst-case latency

Deadline miss rate (>16.67 ms)

Adaptation frequency

## Contributing

We welcome contributions to improve the prediction models, controller design, or experimental framework. Please fork the repository, create a new branch, and submit a pull request with your changes. Ensure your code follows the project's structure and includes appropriate documentation.

Contact

For any questions or inquiries, please contact:
onyeka.nwobodo@polsl.pl
